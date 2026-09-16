using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Muralis.Desktop.Icons;

/// <summary>
/// The desktop canvas' icon store. It keeps resolved icons by target and pixel size, so nothing is
/// ever read from the shell twice, and reads them on a thread of its own — the shell thread must
/// never wait on the shell. Callers ask with <see cref="Request"/> and pick results up with
/// <see cref="TryGet"/>; <see cref="BitmapArrived"/> says when there is something new to pick up.
/// </summary>
/// <remarks>
/// <para>
/// The worker is a single STA thread: the shell's image lists want COM initialized, one thread keeps
/// the shell calls serial — a burst of fifty items is a queue, not a stampede — and the readers run
/// in the order items were requested. What the cache holds is the pixels as read, because that is
/// the shape a surface upload wants; the shell thread is the only one that uploads them. The thread
/// lives as long as the cache, which is as long as the canvas content, so remounts never touch it.
/// </para>
/// <para>
/// Entries are held until the byte budget is passed, and the longest-unused ones leave first: icons
/// that keep being shown stay, and a layout with hundreds of items cannot grow the cache without
/// bound. Evicting never breaks a picture that is on screen — a mounted item already owns its
/// uploaded surface; only a later mount would ask again.
/// </para>
/// </remarks>
internal sealed class IconBitmapCache : IDisposable
{
    /// <summary>Roughly a thousand jumbo icons' pixels; small icons weigh proportionally less.</summary>
    private const long ByteBudget = 32L * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly Func<string, int, IconBitmap?> _read;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly HashSet<string> _pending = [];
    private readonly BlockingCollection<IconRequest> _queue = [];
    private readonly Thread _worker;

    private long _clock;
    private long _bytes;
    private bool _disposed;

    internal IconBitmapCache(ILogger logger)
        : this(logger, ShellIconReader.Read)
    {
    }

    /// <summary>The reader is injectable so the cache can be tested without the shell.</summary>
    internal IconBitmapCache(ILogger logger, Func<string, int, IconBitmap?> read)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(read);

        _logger = logger;
        _read = read;
        _worker = new Thread(Work)
        {
            Name = "Muralis icon reader",
            IsBackground = true,
        };

        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>Raised on the worker thread when a requested icon has just been stored.</summary>
    internal event Action? BitmapArrived;

    internal int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal long ByteCount
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>The icon for a target at a wanted size, if it has already been read.</summary>
    internal IconBitmap? TryGet(string path, int wantedPixels)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(KeyFor(path, wantedPixels), out var entry))
            {
                return null;
            }

            entry.LastUsed = ++_clock;
            return entry.Bitmap;
        }
    }

    /// <summary>
    /// Asks for an icon. A request for one that is already held or already queued is dropped, so the
    /// shell is asked once per icon however often the canvas asks.
    /// </summary>
    internal void Request(string path, int wantedPixels)
    {
        if (string.IsNullOrWhiteSpace(path) || wantedPixels <= 0)
        {
            return;
        }

        var key = KeyFor(path, wantedPixels);
        lock (_gate)
        {
            if (_disposed || _entries.ContainsKey(key) || !_pending.Add(key))
            {
                return;
            }
        }

        try
        {
            _queue.Add(new IconRequest(key, path, wantedPixels));
        }
        catch (InvalidOperationException)
        {
            // The queue closed between the check above and the add: the cache is going away.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _queue.CompleteAdding();
        if (!_worker.Join(TimeSpan.FromSeconds(2)))
        {
            // A shell call is taking longer than it should; the thread is a background one and the
            // process can still exit without it.
            _logger.LogDebug("The icon reader is still busy while the cache is being disposed");
        }

        _queue.Dispose();
    }

    private static string KeyFor(string path, int wantedPixels) => $"{path}@{wantedPixels}";

    private void Work()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable())
            {
                IconBitmap? bitmap = null;
                try
                {
                    bitmap = _read(request.Path, request.WantedPixels);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The icon of {Path} could not be read from the shell", request.Path);
                }

                var stored = false;
                lock (_gate)
                {
                    _pending.Remove(request.Key);
                    if (bitmap is not null && !_disposed)
                    {
                        Store(request.Key, bitmap);
                        stored = true;
                    }
                }

                if (stored)
                {
                    BitmapArrived?.Invoke();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The queue went away with the cache; the thread is on its way out.
        }
    }

    /// <summary>Stores one icon and evicts the longest-unused entries until the budget fits again.</summary>
    private void Store(string key, IconBitmap bitmap)
    {
        if (_entries.Remove(key, out var replaced))
        {
            _bytes -= replaced.Bitmap.ByteCount;
        }

        _entries[key] = new Entry(bitmap, ++_clock);
        _bytes += bitmap.ByteCount;

        while (_bytes > ByteBudget && _entries.Count > 1)
        {
            var oldest = _entries.OrderBy(pair => pair.Value.LastUsed).First();

            // The entry just stored is the most recently used, so it is never the one leaving here.
            _entries.Remove(oldest.Key);
            _bytes -= oldest.Value.Bitmap.ByteCount;
            _logger.LogDebug("The icon of {Key} left the cache to stay inside its budget", oldest.Key);
        }
    }

    private readonly record struct IconRequest(string Key, string Path, int WantedPixels);

    private sealed class Entry(IconBitmap bitmap, long lastUsed)
    {
        internal IconBitmap Bitmap { get; } = bitmap;

        internal long LastUsed { get; set; } = lastUsed;
    }
}
