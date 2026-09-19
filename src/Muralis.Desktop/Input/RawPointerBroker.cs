using Muralis.Core.Diagnostics;

namespace Muralis.Desktop.Input;

/// <summary>
/// The process-wide owner of mouse raw input. Consumers receive absolute screen positions without owning,
/// replacing or unregistering the native device registration themselves.
/// </summary>
public sealed class RawPointerBroker : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IRawPointerRegistration> _sourceFactory;
    private readonly Dictionary<long, Action<int, int>> _consumers = [];
    private Action<int, int>[] _fanout = [];
    private IRawPointerRegistration? _source;
    private long _nextConsumer;
    private bool _disposed;

    public RawPointerBroker()
        : this(static () => new RawPointerWindow())
    {
    }

    internal RawPointerBroker(Func<IRawPointerRegistration> sourceFactory)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
    }

    public bool IsRegistered
    {
        get
        {
            lock (_gate)
            {
                return _source?.IsRegistered ?? false;
            }
        }
    }

    public int ConsumerCount
    {
        get
        {
            lock (_gate)
            {
                return _consumers.Count;
            }
        }
    }

    public long Reports => _source?.Reports ?? 0;

    public long Messages => _source?.Messages ?? 0;

    public IDisposable Subscribe(Action<int, int> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var id = ++_nextConsumer;
            _consumers.Add(id, consumer);
            RebuildFanout();

            if (_source is null)
            {
                _source = _sourceFactory();
                _source.Moved += OnMoved;
                _ = _source.Open();
            }

            return new ConsumerLease(this, id);
        }
    }

    public void RequestDump() => _source?.RequestDump();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _consumers.Clear();
            RebuildFanout();
            StopSource();
        }
    }

    private void Unsubscribe(long id)
    {
        lock (_gate)
        {
            if (!_consumers.Remove(id))
            {
                return;
            }

            RebuildFanout();
            if (_consumers.Count == 0)
            {
                StopSource();
            }
        }
    }

    private void StopSource()
    {
        if (_source is null)
        {
            return;
        }

        _source.Moved -= OnMoved;
        _source.Dispose();
        _source = null;
    }

    private void RebuildFanout() => Volatile.Write(ref _fanout, [.. _consumers.Values]);

    private void OnMoved(int screenX, int screenY)
    {
        // The immutable snapshot is rebuilt only when consumers change. The WM_INPUT path performs no lock,
        // LINQ or allocation, and one failing consumer cannot stop the remaining formal product paths.
        var consumers = Volatile.Read(ref _fanout);
        for (var i = 0; i < consumers.Length; i++)
        {
            try
            {
                consumers[i](screenX, screenY);
            }
            catch (Exception ex)
            {
                if (DropProfile.IsEnabled)
                {
                    DropProfile.Mark(
                        "raw.consumer.failure",
                        0,
                        "\"type\":\"" + ex.GetType().Name.Replace("\"", "'", StringComparison.Ordinal) + "\"");
                }
            }
        }
    }

    private sealed class ConsumerLease(RawPointerBroker owner, long id) : IDisposable
    {
        private RawPointerBroker? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}

internal interface IRawPointerRegistration : IDisposable
{
    event Action<int, int>? Moved;

    bool IsRegistered { get; }

    long Reports { get; }

    long Messages { get; }

    bool Open();

    void RequestDump();
}
