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
internal static class SingleInstanceGuard
{
    private const string MutexName = @"Local\Muralis.SingleInstance";

    private static readonly Lock Gate = new();
    private static Mutex? _ownership;
    private static bool? _isPrimary;

    /// <summary>
    /// True when this process is the one instance that may run. False means another instance owns
    /// the session and has been asked to show itself. The answer is decided once per process, and
    /// the mutex is kept referenced so the ownership survives until the process exits.
    /// </summary>
    internal static bool IsPrimaryInstance()
    {
        lock (Gate)
        {
            if (_isPrimary is { } known)
            {
                return known;
            }

            _isPrimary = Acquire();
            if (!_isPrimary.Value)
            {
                SignalExistingInstance();
            }

            return _isPrimary.Value;
        }
    }

    private static bool Acquire()
    {
        try
        {
            // createdNew is false while another process holds the name: exactly the
            // "already running" case. A process that died leaves no handle behind, so the
            // next launch creates the mutex fresh instead of being locked out.
            _ownership = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException ex)
        {
            // An elevated or other-user instance owns the name. Refusing to start would be
            // worse than a second window, so the app continues.
            Log.Warning(ex, "The single-instance mutex could not be opened; starting anyway");
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            // The fresh process still holds the foreground right Windows grants to a launch
            // target; passing it on lets the running instance bring itself to the front.
            var granted = ShellInterop.AllowSetForegroundWindow(ShellInterop.AsfwAny);

            if (!ShellInterop.PostMessageW(ShellInterop.HwndBroadcast, ShellInterop.ActivationRequested, 0, 0))
            {
                Log.Warning("The running instance could not be woken (error {Error})", Marshal.GetLastWin32Error());
                return;
            }

            Log.Information("Woke the running instance (foreground permission granted: {Granted})", granted);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The running instance could not be woken");
        }
    }
}
