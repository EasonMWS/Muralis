using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Muralis.Core.Helpers;

namespace Muralis.Core.Diagnostics;

/// <summary>
/// Per-segment timing for the dock's drop path, off unless <c>MURALIS_DOCK_PROFILE=1</c> is set in the
/// environment of the process. It exists because the drop was measured from outside — a whole-process
/// processor-time delta over the second after the release — which can say how much the drop costs but
/// not which part of it costs it.
/// </summary>
/// <remarks>
/// <para>
/// The cost of leaving this in is one bool read per call site and no allocation: a disabled
/// <see cref="Scope"/> is a default struct whose <see cref="Scope.Dispose"/> returns immediately. The
/// scopes are deliberately not named after the code that contains them but after the stage a reader of
/// the measurement would ask about, so the report and the numbers share their vocabulary.
/// </para>
/// <para>
/// Records go to <c>logs/dock-drop-profile.jsonl</c>, one JSON object per line, timestamped from the
/// process start. A line carries the drop it belongs to, so a reader can add up a stage across a whole
/// run; a sample that arrives after its drop was closed is kept and marked, which is what happens to a
/// write that outlives the release that scheduled it.
/// </para>
/// </remarks>
public static class DropProfile
{
    /// <summary>The environment variable that turns recording on.</summary>
    public const string EnabledVariable = "MURALIS_DOCK_PROFILE";

    // A run is a benchmark, not a session: a few hundred drops is far more than any measurement asks
    // for, and the cap is what keeps a forgotten environment variable from filling the log folder.
    private const int MaximumDrops = 600;
    private const int MaximumSamples = 40000;

    private static readonly object Gate = new();
    private static readonly StringBuilder Pending = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static Process? _self;
    private static int _current;
    private static int _drops;
    private static int _samples;
    private static long _dropStarted;
    private static double _dropCpuMs;
    private static long _dropAllocated;
    private static ProcessThread? _dropThread;
    private static double _dropThreadCpuMs;
    private static int _dropThreadId;
    private static ProcessThread? _knownThread;
    private static int _knownThreadId;
    private static bool _open;
    private static bool _created;
    private static bool _exhausted;

    /// <summary>Whether this process was asked to record.</summary>
    public static bool IsEnabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal);

    /// <summary>Where the records are written. Overwritten at the start of each profiled run.</summary>
    public static string FilePath { get; } = Path.Combine(AppPaths.LogsDirectory, "dock-drop-profile.jsonl");

    /// <summary>Milliseconds since the profiler was first touched, which is close to process start.</summary>
    public static double ElapsedMs => Clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// Processor time the whole process has used so far. The same quantity the external measurement reads
    /// out of the process, which is what makes the two comparable.
    /// </summary>
    public static double ProcessCpuMs => CpuMs();

    /// <summary>The drop being recorded, or the last one recorded. 0 before the first.</summary>
    public static int CurrentDrop
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Whether a drop is still open — that is, whether a release is still being carried out.</summary>
    public static bool IsOpen
    {
        get
        {
            lock (Gate)
            {
                return _open;
            }
        }
    }

    /// <summary>
    /// Opens a drop. Called once per release that actually carried something, so a click that only
    /// pressed and let go never opens one.
    /// </summary>
    public static void Begin(string label)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (Gate)
        {
            if (_exhausted || _drops >= MaximumDrops)
            {
                _exhausted = true;
                return;
            }

            if (_open)
            {
                // A release while one is still open means the previous one was never closed. Close it
                // rather than recording two drops into one window.
                CloseDrop();
            }

            _current++;
            _open = true;
            _dropStarted = Stopwatch.GetTimestamp();
            _dropCpuMs = CpuMs();
            var threadId = CurrentThreadId();
            _dropThread = threadId == 0 ? null : ThreadOf(threadId);
            _dropThreadId = _dropThread?.Id ?? 0;
            _dropThreadCpuMs = ThreadCpuMs(_dropThread);
            _dropAllocated = GC.GetAllocatedBytesForCurrentThread();
            Append("begin", Field("label", label) + ",\"th\":" + _dropThreadId.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Closes the open drop and writes everything recorded for it out. Idempotent, because more than one
    /// path ends a drag and only the first of them is the one that happened.
    /// </summary>
    public static void End()
    {
        if (!IsEnabled)
        {
            return;
        }

        string? payload;
        lock (Gate)
        {
            if (!_open)
            {
                payload = TakePending();
            }
            else
            {
                CloseDrop();
                payload = TakePending();
            }
        }

        Write(payload);
    }

    /// <summary>
    /// Times the caller until the returned scope is disposed, recording wall clock, the processor time the
    /// whole process used inside it, and what this thread allocated.
    /// </summary>
    public static Scope Measure(string name) => IsEnabled ? new Scope(name) : default;

    /// <summary>
    /// Records a number measured by the caller rather than by a scope — the frame and layout samples,
    /// which happen outside any call the profiler could wrap.
    /// </summary>
    public static void Mark(string name, double wallMs, string? extra = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        string? payload;
        lock (Gate)
        {
            Append("mark", Field("name", name) + ",\"wall\":" + Fixed(wallMs) + (extra is null ? string.Empty : "," + extra));
            payload = TakePending();
        }

        Write(payload);
    }

    /// <summary>Notes an event that is not a duration: an icon pass, a dispatched callback, a decision.</summary>
    public static void Event(string label, string? extra = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (Gate)
        {
            Append("note", Field("label", label) + (extra is null ? string.Empty : "," + extra));
        }
    }

    /// <summary>Processor time the process has used, which is what the external measurement reports too.</summary>
    internal static double CpuMs() =>
        (_self ??= Process.GetCurrentProcess()).TotalProcessorTime.TotalMilliseconds;

    /// <summary>
    /// Processor time the calling thread has used, or NaN where the platform will not say. The process
    /// figure answers "how much work was there"; this one answers "how much of it landed on the thread
    /// that also draws and reads input", which is what a stalled frame is.
    /// </summary>
    public static double CurrentThreadCpuMs
    {
        get
        {
            var id = CurrentThreadId();
            return id == 0 ? double.NaN : ThreadCpuMs(ThreadOf(id));
        }
    }

    /// <summary>Milliseconds between two <see cref="Stopwatch.GetTimestamp"/> readings.</summary>
    internal static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // The thread's own identity is the only reliable way to ask the runtime for it: the managed id is
    // not the one the process table uses.
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static uint CurrentThreadId()
    {
        try
        {
            return GetCurrentThreadId();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Core targets a platform-neutral framework even though the app only runs on Windows, so the
            // absence of this call means "no per-thread figure", never a failed drop.
            return 0;
        }
    }

    private static ProcessThread? ThreadOf(uint nativeId)
    {
        if (_knownThread is not null && _knownThreadId == (int)nativeId)
        {
            return _knownThread;
        }

        ProcessThread? found = null;
        try
        {
            foreach (ProcessThread candidate in (_self ??= Process.GetCurrentProcess()).Threads)
            {
                if (candidate.Id == (int)nativeId)
                {
                    found = candidate;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }

        if (found is not null)
        {
            _knownThread = found;
            _knownThreadId = (int)nativeId;
        }

        return found;
    }

    private static double ThreadCpuMs(ProcessThread? thread)
    {
        if (thread is null)
        {
            return double.NaN;
        }

        try
        {
            return thread.TotalProcessorTime.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            // A thread that has ended cannot be asked, and a missing figure is not a reason to fail a drop.
            return double.NaN;
        }
    }

    private static void CloseDrop()
    {
        _open = false;
        _drops++;

        // Read against the thread the drop was opened on rather than the one closing it, so the figure
        // means "what the release cost the UI thread" no matter which path ended the drag.
        var threadCpu = ThreadCpuMs(_dropThread);
        Append(
            "end",
            "\"drop\":" + _current.ToString(CultureInfo.InvariantCulture)
            + ",\"wall\":" + Fixed(TicksToMs(Stopwatch.GetTimestamp() - _dropStarted))
            + ",\"cpu\":" + Fixed(CpuMs() - _dropCpuMs)
            + ",\"uiCpu\":" + Number(threadCpu - _dropThreadCpuMs)
            + ",\"alloc\":" + (GC.GetAllocatedBytesForCurrentThread() - _dropAllocated).ToString(CultureInfo.InvariantCulture)
            + ",\"th\":" + _dropThreadId.ToString(CultureInfo.InvariantCulture));

        _dropThread = null;
    }

    internal static void Record(string name, long started, double cpuMs, double threadCpuMs, long allocated, int thread, int drop)
    {
        var wall = TicksToMs(Stopwatch.GetTimestamp() - started);
        var cpu = CpuMs() - cpuMs;
        var exited = Environment.CurrentManagedThreadId;

        // Allocation is a per-thread counter, so a scope that awaited and resumed elsewhere has no figure
        // to report: the difference of two threads' counters was never a quantity, and a report that shows
        // a negative allocation is worse than a report that shows none.
        var sameThread = exited == thread;
        var alloc = sameThread ? GC.GetAllocatedBytesForCurrentThread() - allocated : 0;

        lock (Gate)
        {
            if (_samples >= MaximumSamples)
            {
                _exhausted = true;
                return;
            }

            _samples++;
            Append(
                "seg",
                Field("name", name)
                + ",\"wall\":" + Fixed(wall)
                + ",\"cpu\":" + Fixed(cpu)
                // The thread figure is the honest one for a stage that ran while something else was also
                // working: the process figure would hand this stage whatever a background thread did.
                + ",\"thCpu\":" + Number(threadCpuMs)
                + ",\"alloc\":" + (sameThread ? alloc.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"th\":" + thread.ToString(CultureInfo.InvariantCulture)
                // A scope that spans an await may not come back on the thread it started on, and nothing
                // about the two threads can be subtracted, so which one reported it is said out loud.
                + (sameThread ? string.Empty : ",\"th2\":" + exited.ToString(CultureInfo.InvariantCulture))
                + ",\"drop\":" + drop.ToString(CultureInfo.InvariantCulture)
                + ",\"late\":" + (_open ? "false" : "true"));
        }
    }

    private static void Append(string kind, string fields)
    {
        Pending.Append("{\"t\":")
            .Append(Fixed(ElapsedMs))
            .Append(",\"ev\":\"")
            .Append(kind)
            .Append("\",\"drop\":")
            .Append(_current.ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append(fields)
            .Append("}\n");
    }

    private static string TakePending()
    {
        if (Pending.Length == 0)
        {
            return string.Empty;
        }

        var text = Pending.ToString();
        Pending.Clear();
        return text;
    }

    private static void Write(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || !IsEnabled)
        {
            return;
        }

        // The write is itself timed so a reader cannot mistake it for the app being slow: it is a few
        // hundred microseconds of file I/O sitting at the end of the drop on purpose.
        using var measured = Measure("profile.write");
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            if (_created)
            {
                File.AppendAllText(FilePath, payload);
            }
            else
            {
                File.WriteAllText(FilePath, payload);
                _created = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A profile that cannot be written is not a reason for the drop to fail.
            _exhausted = true;
        }
    }

    private static string Field(string name, string value) =>
        "\"" + name + "\":\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Fixed(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    // A figure the platform would not give is written as null rather than NaN: the file is JSON, and a
    // reader should be able to tell "no number" from "zero".
    private static string Number(double value) =>
        double.IsNaN(value) ? "null" : Fixed(value);

    /// <summary>One timed stage of a drop. Does nothing at all when the profiler is off.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _started;
        private readonly double _cpuMs;
        private readonly double _threadCpuMs;
        private readonly uint _nativeThread;
        private readonly long _allocated;
        private readonly int _thread;
        private readonly int _drop;

        internal Scope(string name)
        {
            _name = name;
            _started = Stopwatch.GetTimestamp();
            _cpuMs = CpuMs();
            _nativeThread = CurrentThreadId();
            _threadCpuMs = _nativeThread == 0 ? double.NaN : ThreadCpuMs(ThreadOf(_nativeThread));
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _thread = Environment.CurrentManagedThreadId;
            _drop = CurrentDrop;
        }

        public void Dispose()
        {
            if (_name is null)
            {
                return;
            }

            // A stage that awaited and came back somewhere else has no thread cost of its own to report:
            // subtracting two different threads would produce a number that means nothing.
            var threadCpu = double.NaN;
            if (_nativeThread != 0 && CurrentThreadId() == _nativeThread)
            {
                threadCpu = ThreadCpuMs(ThreadOf(_nativeThread)) - _threadCpuMs;
            }

            Record(_name, _started, _cpuMs, threadCpu, _allocated, _thread, _drop);
        }
    }
}
