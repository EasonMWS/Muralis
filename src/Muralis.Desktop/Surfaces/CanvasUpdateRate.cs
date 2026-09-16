namespace Muralis.Desktop.Surfaces;

/// <summary>
/// Counts canvas updates in a sliding second so the diagnostics can show a truthful rate: at rest
/// it decays to zero on its own, because no update ticked and every bucket has aged out. Sliding
/// buckets keep it allocation-free and timer-free — snapshot reads simply look at the last eight
/// 125 ms buckets.
/// </summary>
internal sealed class CanvasUpdateRate
{
    private const int BucketMilliseconds = 125;
    private const int BucketCount = 8;

    private readonly long[] _bucketStart = new long[BucketCount];
    private readonly int[] _counts = new int[BucketCount];
    private long _total;

    /// <summary>Records one update that just happened.</summary>
    internal void Bump(long nowMilliseconds)
    {
        var index = (int)(nowMilliseconds / BucketMilliseconds % BucketCount);
        if (_bucketStart[index] != nowMilliseconds / BucketMilliseconds)
        {
            _bucketStart[index] = nowMilliseconds / BucketMilliseconds;
            _counts[index] = 0;
        }

        _counts[index]++;
        _total++;
    }

    /// <summary>Updates per second over the last second; zero once nothing has happened for that long.</summary>
    internal double PerSecond(long nowMilliseconds)
    {
        var currentBucket = nowMilliseconds / BucketMilliseconds;
        var total = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            var age = currentBucket - _bucketStart[i];
            if (age is >= 0 and < BucketCount)
            {
                total += _counts[i];
            }
        }

        return Math.Round(total * (1000.0 / (BucketCount * BucketMilliseconds)), 1);
    }

    /// <summary>Updates since the canvas started, for the overlay's lifetime counter.</summary>
    internal long Total => _total;
}
