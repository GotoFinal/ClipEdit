using ClipEdit.Domain.Timeline;

namespace ClipEdit.Domain.Tests.Timeline;

public sealed class FrameRateTests
{
    [Fact]
    public void Default_value_is_zero_frames_per_second()
    {
        FrameRate value = default;

        Assert.True(value.IsZero);
        Assert.Equal(1, value.Denominator);
    }

    [Fact]
    public void Constructor_preserves_ntsc_rate_exactly()
    {
        var value = new FrameRate(24_000, 1_001);

        Assert.Equal(24_000, value.Numerator);
        Assert.Equal(1_001, value.Denominator);
        Assert.Equal(23.976, value.FramesPerSecond, precision: 3);
    }

    [Fact]
    public void Constructor_normalizes_the_rate()
    {
        Assert.Equal(new FrameRate(30, 1), new FrameRate(60, 2));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Constructor_rejects_invalid_values(long numerator, int denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(numerator, denominator));
    }
}
