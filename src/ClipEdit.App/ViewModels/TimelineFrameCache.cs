using ClipEdit.Domain.Geometry;

namespace ClipEdit.App.ViewModels;

internal readonly record struct TimelineFrameCacheKey(
    string SourcePath,
    int VideoStreamIndex,
    PixelSize MaximumSize,
    long BucketDurationMicroseconds,
    long BucketIndex)
{
    public double TimestampSeconds =>
        ((BucketIndex + 0.5) * BucketDurationMicroseconds) / 1_000_000d;

    public static TimelineFrameCacheKey Create(
        string sourcePath,
        int videoStreamIndex,
        PixelSize maximumSize,
        double bucketDurationSeconds,
        double sourceSeconds)
    {
        var durationMicroseconds = Math.Max(
            1,
            checked((long)Math.Round(bucketDurationSeconds * 1_000_000d)));
        var sourceMicroseconds = Math.Max(
            0,
            checked((long)Math.Floor(sourceSeconds * 1_000_000d)));
        return new TimelineFrameCacheKey(
            NormalizePath(sourcePath),
            videoStreamIndex,
            maximumSize,
            durationMicroseconds,
            sourceMicroseconds / durationMicroseconds);
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }
}

/// <summary>
/// Memory-only LRU for encoded timeline frames. Both entry count and byte use are
/// bounded, so cache growth is independent of source duration and file size.
/// </summary>
internal sealed class TimelineFrameCache
{
    public const int DefaultMaximumEntries = 384;
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;
    public const double MinimumBucketDurationSeconds = 0.125;
    public const double MaximumBucketDurationSeconds = 256;

    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private readonly Dictionary<TimelineFrameCacheKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _leastRecentlyUsed = [];
    private long _byteCount;

    public TimelineFrameCache(
        int maximumEntries = DefaultMaximumEntries,
        long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        _maximumEntries = maximumEntries;
        _maximumBytes = maximumBytes;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public long ByteCount
    {
        get
        {
            lock (_gate)
            {
                return _byteCount;
            }
        }
    }

    public bool TryGet(TimelineFrameCacheKey key, out byte[] encodedImage)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                encodedImage = [];
                return false;
            }

            Touch(node);
            encodedImage = node.Value.EncodedImage;
            return true;
        }
    }

    public bool TryGetNearest(
        string sourcePath,
        int videoStreamIndex,
        double sourceSeconds,
        double maximumDistanceSeconds,
        out TimelineFrameCacheKey key,
        out byte[] encodedImage)
    {
        var normalizedPath = TimelineFrameCacheKey.Create(
            sourcePath,
            videoStreamIndex,
            new PixelSize(1, 1),
            1,
            0).SourcePath;
        lock (_gate)
        {
            LinkedListNode<Entry>? nearest = null;
            var nearestDistance = Math.Max(0, maximumDistanceSeconds);
            foreach (var candidate in _entries.Values)
            {
                if (candidate.Value.Key.VideoStreamIndex != videoStreamIndex ||
                    !string.Equals(candidate.Value.Key.SourcePath, normalizedPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var distance = Math.Abs(candidate.Value.Key.TimestampSeconds - sourceSeconds);
                if (distance <= nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            if (nearest is null)
            {
                key = default;
                encodedImage = [];
                return false;
            }

            Touch(nearest);
            key = nearest.Value.Key;
            encodedImage = nearest.Value.EncodedImage;
            return true;
        }
    }

    public void Set(TimelineFrameCacheKey key, ReadOnlyMemory<byte> encodedImage)
    {
        if (encodedImage.IsEmpty || encodedImage.Length > _maximumBytes)
        {
            return;
        }

        var owned = encodedImage.ToArray();
        lock (_gate)
        {
            if (_entries.Remove(key, out var previous))
            {
                _leastRecentlyUsed.Remove(previous);
                _byteCount -= previous.Value.EncodedImage.Length;
            }

            var node = _leastRecentlyUsed.AddFirst(new Entry(key, owned));
            _entries.Add(key, node);
            _byteCount += owned.Length;
            while (_entries.Count > _maximumEntries || _byteCount > _maximumBytes)
            {
                EvictLeastRecentlyUsed();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _leastRecentlyUsed.Clear();
            _byteCount = 0;
        }
    }

    public static double ChooseBucketDuration(double targetCellDurationSeconds)
    {
        if (!double.IsFinite(targetCellDurationSeconds) || targetCellDurationSeconds <= 0)
        {
            return MinimumBucketDurationSeconds;
        }

        var bounded = Math.Clamp(
            targetCellDurationSeconds,
            MinimumBucketDurationSeconds,
            MaximumBucketDurationSeconds);
        return Math.Max(
            MinimumBucketDurationSeconds,
            Math.Pow(2, Math.Floor(Math.Log2(bounded))));
    }

    private void Touch(LinkedListNode<Entry> node)
    {
        _leastRecentlyUsed.Remove(node);
        _leastRecentlyUsed.AddFirst(node);
    }

    private void EvictLeastRecentlyUsed()
    {
        var node = _leastRecentlyUsed.Last;
        if (node is null)
        {
            return;
        }

        _leastRecentlyUsed.RemoveLast();
        _entries.Remove(node.Value.Key);
        _byteCount -= node.Value.EncodedImage.Length;
    }

    private sealed record Entry(TimelineFrameCacheKey Key, byte[] EncodedImage);
}
