using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Icons;

/// <summary>Asynchronous 48px-capable Shell icon extraction on one dedicated STA with a small cache.</summary>
public sealed class ShellIconProvider : IShellIconProvider
{
    private readonly ILogger<ShellIconProvider> _logger;
    private readonly ConcurrentDictionary<string, ShellIconData?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Request> _requests = [];
    private readonly Thread _worker;
    private bool _disposed;

    public ShellIconProvider(ILogger<ShellIconProvider> logger)
    {
        _logger = logger;
        _worker = new Thread(Work) { IsBackground = true, Name = "Muralis Shelf icon reader" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public int CacheCount => _cache.Count;

    public Task<ShellIconData?> GetAsync(string path, int pixelSize = 48, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(path) || pixelSize <= 0)
        {
            return Task.FromResult<ShellIconData?>(null);
        }

        var key = CacheKey(path, pixelSize);
        if (_cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        var completion = new TaskCompletionSource<ShellIconData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add(new Request(key, path, pixelSize, completion), cancellationToken);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private void Work()
    {
        var entered = NativeMethods.CoInitializeEx(nint.Zero, NativeMethods.CoInitApartmentThreaded) is 0 or 1;
        try
        {
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                try
                {
                    if (!_cache.TryGetValue(request.Key, out var icon))
                    {
                        var bitmap = ShellIconReader.Read(request.Path, request.PixelSize);
                        icon = bitmap is null ? null : new ShellIconData(bitmap.Width, bitmap.Height, bitmap.Pixels);
                        if (icon is not null)
                        {
                            _cache.TryAdd(request.Key, icon);
                        }
                        else
                        {
                            _logger.LogDebug(
                                "The Shell returned no usable {PixelSize}px icon for {Path}; the Dock will keep its fallback",
                                request.PixelSize,
                                request.Path);
                        }
                    }

                    request.Completion.TrySetResult(icon);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The Shelf icon for {Path} could not be extracted", request.Path);
                    request.Completion.TrySetResult(null);
                }
            }
        }
        finally
        {
            if (entered)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _requests.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        _requests.Dispose();
    }

    /// <summary>
    /// Size is part of identity so a low-resolution request cannot poison a high-resolution one.
    /// Last-write time lets an edited shortcut/custom icon refresh without restarting Muralis.
    /// </summary>
    internal static string CacheKey(string path, int pixelSize)
    {
        var fullPath = Path.GetFullPath(path);
        long stamp;
        try
        {
            stamp = File.Exists(fullPath) || Directory.Exists(fullPath)
                ? File.GetLastWriteTimeUtc(fullPath).Ticks
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = 0;
        }

        return $"{fullPath}@{pixelSize}@{stamp}";
    }

    private sealed record Request(
        string Key,
        string Path,
        int PixelSize,
        TaskCompletionSource<ShellIconData?> Completion);
}
