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

        var key = $"{Path.GetFullPath(path)}@{pixelSize}";
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
                        _cache.TryAdd(request.Key, icon);
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

    private sealed record Request(
        string Key,
        string Path,
        int PixelSize,
        TaskCompletionSource<ShellIconData?> Completion);
}
