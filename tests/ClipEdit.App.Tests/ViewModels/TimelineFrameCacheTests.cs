using ClipEdit.App.ViewModels;
using ClipEdit.Domain.Geometry;

namespace ClipEdit.App.Tests.ViewModels;

public sealed class TimelineFrameCacheTests
{
    [Fact]
    public void Bucket_duration_uses_reusable_power_of_two_levels()
    {
        Assert.Equal(0.125, TimelineFrameCache.ChooseBucketDuration(0.01));
        Assert.Equal(4, TimelineFrameCache.ChooseBucketDuration(7.9));
        Assert.Equal(64, TimelineFrameCache.ChooseBucketDuration(100));
    }

    [Fact]
    public void Cache_is_bounded_by_entry_count_and_bytes()
    {
        var cache = new TimelineFrameCache(maximumEntries: 2, maximumBytes: 5);
        var path = Path.Combine(Path.GetTempPath(), "bounded-cache.mkv");
        var size = new PixelSize(240, 120);
        var first = TimelineFrameCacheKey.Create(path, 0, size, 1, 0);
        var second = TimelineFrameCacheKey.Create(path, 0, size, 1, 1);
        var third = TimelineFrameCacheKey.Create(path, 0, size, 1, 2);

        cache.Set(first, new byte[] { 1, 1 });
        cache.Set(second, new byte[] { 2, 2 });
        Assert.True(cache.TryGet(first, out _));
        cache.Set(third, new byte[] { 3, 3 });

        Assert.Equal(2, cache.Count);
        Assert.Equal(4, cache.ByteCount);
        Assert.True(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(third, out _));
    }

    [Fact]
    public void Nearest_lookup_reuses_a_warm_timeline_frame_for_hover()
    {
        var cache = new TimelineFrameCache();
        var path = Path.Combine(Path.GetTempPath(), "hover-cache.mkv");
        var key = TimelineFrameCacheKey.Create(path, 2, new PixelSize(240, 120), 4, 10);
        cache.Set(key, new byte[] { 4, 2 });

        var found = cache.TryGetNearest(path, 2, 9.8, 3, out var foundKey, out var encoded);

        Assert.True(found);
        Assert.Equal(key, foundKey);
        Assert.Equal(new byte[] { 4, 2 }, encoded);
    }
}
