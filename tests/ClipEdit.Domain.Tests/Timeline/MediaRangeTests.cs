using ClipEdit.Domain.Timeline;

namespace ClipEdit.Domain.Tests.Timeline;

public sealed class MediaRangeTests
{
    [Fact]
    public void Default_value_is_an_empty_range_at_zero()
    {
        MediaRange range = default;

        Assert.Equal(MediaTime.Zero, range.Start);
        Assert.True(range.IsEmpty);
    }

    [Fact]
    public void Range_is_half_open()
    {
        var range = new MediaRange(new MediaTime(1, 2), new MediaTime(3, 2));

        Assert.True(range.Contains(new MediaTime(1, 2)));
        Assert.True(range.Contains(new MediaTime(149, 100)));
        Assert.False(range.Contains(new MediaTime(3, 2)));
    }

    [Fact]
    public void Duration_is_exact()
    {
        var range = new MediaRange(new MediaTime(1, 24), new MediaTime(11, 24));

        Assert.Equal(new MediaTime(5, 12), range.Duration);
    }

    [Fact]
    public void Empty_range_is_valid_but_contains_no_time()
    {
        var range = new MediaRange(MediaTime.Zero, MediaTime.Zero);

        Assert.True(range.IsEmpty);
        Assert.False(range.Contains(MediaTime.Zero));
    }

    [Fact]
    public void Constructor_rejects_an_end_before_the_start()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MediaRange(new MediaTime(2, 1), new MediaTime(1, 1)));
    }
}
