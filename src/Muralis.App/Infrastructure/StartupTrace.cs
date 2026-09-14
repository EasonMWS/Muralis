using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Muralis.App.Infrastructure;

/// <summary>
/// Marks how long each step of the startup path takes, measured from the moment the
/// application class was created. The numbers are written to the log so startup
/// regressions are visible without attaching a profiler.
/// </summary>
internal static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Milliseconds since the application class was created.</summary>
    public static double ElapsedMs => Clock.Elapsed.TotalMilliseconds;

    /// <summary>Milliseconds from process start to the moment the application class was created.</summary>
    public static double ProcessToAppMs { get; } = MeasureProcessToApp();

    public static void Log(ILogger logger, string phase)
    {
        logger.LogInformation("Startup: {Phase} at {ElapsedMs:0} ms", phase, ElapsedMs);
    }

    private static double MeasureProcessToApp()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return 0;
        }
    }
}
