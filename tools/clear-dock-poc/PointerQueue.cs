namespace ClearDockPoc;

/// <summary>
/// Raw pointer reports crossing from the raw source's thread to the window's thread.
/// </summary>
/// <remarks>
/// <para>
/// The broker raises its reports on the raw source's own thread, and the renderer may only compose on the thread
/// that owns the window. This is the crossing point, and it is deliberately a queue of individual samples rather
/// than a single "latest position" field: a fast sweep must produce the positions it passed through, in order,
/// because collapsing them into the latest one is what makes a dock lag its own cursor.
/// </para>
/// <para>
/// Bounded on purpose. A bounded ring with a drop counter fails visibly and cheaply; an unbounded queue turns a
/// stalled consumer into a memory leak, and a growing backlog into motion that plays back history.
/// </para>
/// <para>
/// The producer never blocks: it takes the lock, and if the ring is full the sample is counted and discarded. The
/// consumer drains everything waiting. The window is posted to exactly once per batch, on the transition from
/// empty to non-empty, so a burst of reports cannot queue a burst of messages.
/// </para>
/// </remarks>
internal sealed class PointerQueue(int capacity)
{
    private readonly object _gate = new();
    private readonly PointerSample[] _samples = new PointerSample[capacity];
    private int _head;
    private int _count;
    private bool _postOutstanding;

    /// <summary>Reports accepted into the queue.</summary>
    public long Queued { get; private set; }

    /// <summary>Reports discarded because the consumer had not drained the previous batch.</summary>
    public long Dropped { get; private set; }

    /// <summary>How many samples are waiting right now.</summary>
    public int Depth
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Adds one report. Returns true when the caller should post the window, which happens once per batch.
    /// </summary>
    public bool TryEnqueue(int screenX, int screenY, long timestamp)
    {
        lock (_gate)
        {
            if (_count == _samples.Length)
            {
                Dropped++;
                return false;
            }

            _samples[(_head + _count) % _samples.Length] = new PointerSample(screenX, screenY, timestamp);
            _count++;
            Queued++;

            if (_postOutstanding)
            {
                return false;
            }

            _postOutstanding = true;
            return true;
        }
    }

    /// <summary>
    /// Takes every sample waiting, oldest first. The consumer calls this once per posted message.
    /// </summary>
    public int Drain(List<PointerSample> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        lock (_gate)
        {
            var taken = 0;
            while (_count > 0)
            {
                into.Add(_samples[_head]);
                _head = (_head + 1) % _samples.Length;
                _count--;
                taken++;
            }

            _postOutstanding = false;
            return taken;
        }
    }

    /// <summary>
    /// Drops everything waiting, counting it as lost. Used when the consumer is gone, so that a queue nobody will
    /// read cannot masquerade as a queue that is keeping up.
    /// </summary>
    public void Discard()
    {
        lock (_gate)
        {
            Dropped += _count;
            _count = 0;
            _head = 0;
            _postOutstanding = false;
        }
    }
}

/// <summary>One raw pointer report: a physical screen position and when it was captured.</summary>
internal readonly record struct PointerSample(int ScreenX, int ScreenY, long Timestamp);
