using System.Runtime.InteropServices;
using Muralis.App.Services.Platform;
using Serilog;

namespace Muralis.App.Infrastructure;

/// <summary>
/// Keeps Muralis to one running instance. The first launch owns a per-session named mutex for the
/// rest of the process lifetime; later launches wake the running instance and exit before any
/// service is built, so no second tray icon, rotation timer, database writer or desktop host
/// is ever created.
/// </summary>
/// <remarks>
/// The name is taken as a lock, not read as a flag. A process that is leaving still holds the name
/// for a moment after Windows reports it gone, so a launch in that moment used to look like a
/// second instance: it woke an instance that was already dying and exited itself, and the user saw
/// nothing happen. Two things keep the slot honest now. A launch that finds the name taken asks
/// whether the holder is really there, and the holder answers only once it has actually brought its
/// window forward — an instance that is closing cannot, so its silence is the launch's cue to wait
/// for the name instead, a wait that ends exactly when Windows abandons the mutex of a process that
/// died. And a clean exit releases the name on its way out instead of leaving that to process
/// teardown, so the moment after a normal exit needs no wait at all.
/// </remarks>
internal static class SingleInstanceGuard
{
    private const string MutexName = @"Local\Muralis.SingleInstance";

    /// <summary>Named event a running instance sets to answer a launch asking whether it is there.</summary>
    private const string AckEventName = @"Local\Muralis.SingleInstance.Ack";

    /// <summary>How long a live instance may take to answer the ping before it counts as gone.</summary>
    private const int AckTimeoutMilliseconds = 500;

    /// <summary>How long a launch waits for the name before it behaves like a second instance.</summary>
    private const int HandoverTimeoutMilliseconds = 2000;

    /// <summary>How long one wait for the name lasts before its holder is asked again.</summary>
    private const int TakeoverPollMilliseconds = 50;

    private static readonly Lock Gate = new();
    private static Mutex? _ownership;
    private static bool? _isPrimary;

    /// <summary>
    /// True when this process is the one instance that may run. False means another, answering
    /// instance owns the session and has been asked to show itself. The answer is decided once per
    /// process, and the mutex is kept referenced so ownership survives until <see cref="Release"/>.
    /// </summary>
    internal static bool IsPrimaryInstance()
    {
        lock (Gate)
        {
            if (_isPrimary is { } known)
            {
                return known;
            }

            _isPrimary = TryTakeTheName();
            return _isPrimary.Value;
        }
    }

    /// <summary>
    /// Answers a launch that asked whether this instance can still take over the screen. Called once
    /// the window is really in front; an instance that is closing never gets that far and so stays
    /// silent, which is how the next launch learns to wait for the name instead of believing in it.
    /// </summary>
    internal static void AcknowledgeActivation()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(AckEventName, out var ack))
            {
                using (ack)
                {
                    ack.Set();
                }
            }
        }
        catch (Exception ex)
        {
            // Not answering only costs the next launch a wait; it never harms this instance.
            Log.Debug(ex, "The activation ping could not be answered");
        }
    }

    /// <summary>
    /// Lets go of the name on the way out, so a launch right after this one exits does not have to
    /// wait for Windows to tear the process down. Safe to call at any point, and a no-op when this
    /// process never owned the name.
    /// </summary>
    internal static void Release()
    {
        lock (Gate)
        {
            var mutex = _ownership;
            _ownership = null;
            if (mutex is null)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
            {
                // Owned by a different thread than this one: closing the handle abandons the name
                // just as usefully, because an abandoned name can be taken immediately.
            }

            mutex.Dispose();
        }
    }

    /// <summary>
    /// Takes the single-instance name, waiting out a holder that is on its way out and giving up on
    /// one that never answers.
    /// </summary>
    private static bool TryTakeTheName()
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
        }
        catch (UnauthorizedAccessException ex)
        {
            // An elevated or other-user instance owns the name. Refusing to start would be
            // worse than a second window, so the app continues.
            Log.Warning(ex, "The single-instance mutex could not be opened; starting anyway");
            return true;
        }

        var ack = TryCreateAckEvent();
        var deadline = Environment.TickCount64 + HandoverTimeoutMilliseconds;
        try
        {
            while (true)
            {
                if (TryWait(mutex, 0))
                {
                    _ownership = mutex;
                    return true;
                }

                if (PingAndWaitForAnswer(ack))
                {
                    // Someone answered: a Muralis instance is really running, so this launch is
                    // the second one however long the name stays taken.
                    mutex.Dispose();
                    return false;
                }

                if (Environment.TickCount64 >= deadline)
                {
                    // The name is held by something that will not talk to us. Behaving like a
                    // second instance is the safe answer: the alternative is two Muralis windows.
                    mutex.Dispose();
                    return false;
                }

                TryWait(mutex, TakeoverPollMilliseconds);
            }
        }
        finally
        {
            ack?.Dispose();
        }
    }

    /// <summary>
    /// Waits for the name. A holder that died without letting go leaves an abandoned mutex, and an
    /// abandoned name is exactly the case where waiting for it must succeed at once.
    /// </summary>
    private static bool TryWait(Mutex mutex, int millisecondsTimeout)
    {
        try
        {
            return mutex.WaitOne(millisecondsTimeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>
    /// Pings whoever holds the name and reports whether the holder showed itself. Silence means the
    /// holder is on its way out or is not a Muralis at all; the caller then waits for the name
    /// rather than believing the broadcast arrived somewhere that can still act on it.
    /// </summary>
    private static bool PingAndWaitForAnswer(EventWaitHandle? ack)
    {
        if (ack is null)
        {
            // With no answer channel a live holder cannot be told from a dying one, so the caller
            // keeps waiting for the name instead of trusting a broadcast nobody may have seen.
            return false;
        }

        // Cleared before the ping: a late answer to an earlier launch must not pass for this one's.
        ack.Reset();
        return SignalExistingInstance() && ack.WaitOne(AckTimeoutMilliseconds);
    }

    /// <summary>
    /// Asks whatever is running to show itself. Returns whether the broadcast could be posted; the
    /// cast carries the foreground right this fresh launch still holds.
    /// </summary>
    private static bool SignalExistingInstance()
    {
        try
        {
            var granted = ShellInterop.AllowSetForegroundWindow(ShellInterop.AsfwAny);

            if (!ShellInterop.PostMessageW(ShellInterop.HwndBroadcast, ShellInterop.ActivationRequested, 0, 0))
            {
                Log.Warning("The running instance could not be woken (error {Error})", Marshal.GetLastWin32Error());
                return false;
            }

            Log.Information("Woke the running instance (foreground permission granted: {Granted})", granted);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The running instance could not be woken");
            return false;
        }
    }

    /// <summary>
    /// Opens the channel a running instance answers the ping on. Auto-reset, so one answer serves
    /// exactly one launch; creation is best effort, because a missing channel only costs a wait.
    /// </summary>
    private static EventWaitHandle? TryCreateAckEvent()
    {
        try
        {
            return new EventWaitHandle(initialState: false, EventResetMode.AutoReset, AckEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            Log.Warning(ex, "The activation handshake event could not be opened; the ping cannot be answered");
            return null;
        }
    }
}
