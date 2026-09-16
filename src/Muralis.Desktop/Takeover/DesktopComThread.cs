using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Takeover;

/// <summary>
/// The one thread the shell's desktop view is ever touched from. The view is a COM object with thread
/// affinity — a pointer obtained in one apartment may only be used from that apartment — and the
/// desktop's is a single-threaded one, so the calls have to be made from a thread that entered a
/// single-threaded apartment. This is that thread, and the only one: nothing else in the app needs an
/// apartment of its own, and the shell's own thread is left exactly as it was.
/// </summary>
/// <remarks>
/// <para>
/// Work is queued and run one item at a time, in order, so two calls never overlap and the answer to
/// "who is using the view" is always this thread. Nothing is pumped: the calls all go outwards, and no
/// interface of ours is ever handed to the shell, so there is nothing for the shell to call back into.
/// </para>
/// <para>
/// A thread that cannot enter an apartment — one the runtime already put in a multi-threaded one —
/// reports that once, and every call after it fails rather than quietly doing nothing.
/// </para>
/// </remarks>
internal sealed class DesktopComThread : IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    private bool _enteredApartment;
    private bool _disposed;

    internal DesktopComThread(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Muralis shell view",
        };

        // The apartment has to be asked for before the thread runs: the runtime starts its threads in
        // the multi-threaded apartment, and a thread already in one cannot be moved to a
        // single-threaded apartment afterwards — the attempt comes back as RPC_E_CHANGED_MODE.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // It also has to exist before the first call is queued, so the constructor waits for the thread
        // to say whether it got one instead of the first caller finding out.
        _ready.Wait();
    }

    /// <summary>Whether this thread has an apartment to make the shell's calls in.</summary>
    internal bool IsAvailable => _enteredApartment;

    /// <summary>
    /// Runs one piece of work on the thread and hands back its result. The work is queued behind
    /// whatever is already there, so a caller that has to be sure nothing else is in the middle of the
    /// view can simply await its own turn.
    /// </summary>
    internal Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_disposed || !_enteredApartment)
        {
            completion.SetException(new InvalidOperationException(
                "The shell's desktop view cannot be reached: no single-threaded apartment was available."));
            return completion.Task;
        }

        try
        {
            _work.Add(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            completion.TrySetException(new ObjectDisposedException(nameof(DesktopComThread)));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _work.CompleteAdding();

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("The shell view thread did not end in time");
        }

        _ready.Dispose();
        _work.Dispose();
    }

    private void Run()
    {
        var result = NativeMethods.CoInitializeEx(nint.Zero, NativeMethods.CoInitApartmentThreaded);

        // S_OK means this call created the apartment and has to be balanced; S_FALSE means the thread
        // was already in a single-threaded one, which is the usual answer here and just as good.
        _enteredApartment = result is 0 or 1;
        _ready.Set();

        if (!_enteredApartment)
        {
            _logger.LogError(
                "The shell view thread could not enter a single-threaded apartment ({Result:X8}); the desktop takeover cannot be offered",
                result);
            return;
        }

        try
        {
            foreach (var item in _work.GetConsumingEnumerable())
            {
                item();
            }
        }
        finally
        {
            NativeMethods.CoUninitialize();
        }
    }
}
