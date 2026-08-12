using ClipEdit.Domain.Timeline;

namespace ClipEdit.Domain.Tests.Timeline;

public sealed class MediaTimeTests
{
    [Fact]
    public void Default_value_is_exact_zero()
    {
        MediaTime value = default;

        Assert.Equal(MediaTime.Zero, value);
        Assert.Equal(1, value.Denominator);
    }

    [Theory]
    [InlineData(2, 4, 1, 2)]
    [InlineData(-2, -4, 1, 2)]
    [InlineData(2, -4, -1, 2)]
    [InlineData(0, 90_000, 0, 1)]
    public void Constructor_normalizes_values(
        long numerator,
        int denominator,
        long expectedNumerator,
        int expectedDenominator)
    {
        var value = new MediaTime(numerator, denominator);

        Assert.Equal(expectedNumerator, value.Numerator);
        Assert.Equal(expectedDenominator, value.Denominator);
    }

    [Fact]
    public void Constructor_rejects_a_zero_denominator()
    {
        Assert.Throws<DivideByZeroException>(() => new MediaTime(1, 0));
    }

    [Fact]
    public void Arithmetic_remains_exact_across_different_time_bases()
    {
        var oneFrameAtTwentyFourFps = new MediaTime(1, 24);
        var oneFrameAtThirtyFps = new MediaTime(1, 30);

        var result = oneFrameAtTwentyFourFps + oneFrameAtThirtyFps;

        Assert.Equal(new MediaTime(3, 40), result);
        Assert.Equal(oneFrameAtThirtyFps, result - oneFrameAtTwentyFourFps);
    }

    [Fact]
    public void Comparison_does_not_overflow_long_cross_products()
    {
        var slightlySmaller = new MediaTime(long.MaxValue - 1, int.MaxValue);
        var slightlyLarger = new MediaTime(long.MaxValue, int.MaxValue);

        Assert.True(slightlySmaller < slightlyLarger);
    }

    [Fact]
    public void Arithmetic_throws_when_normalized_result_exceeds_storage_contract()
    {
        var value = new MediaTime(long.MaxValue, 1);

        Assert.Throws<OverflowException>(() => value + new MediaTime(1, 1));
    }

    [Fact]
    public void Scale_preserves_the_exact_time_base()
    {
        var timeBase = new MediaTime(1, 90_000);

        var result = timeBase * 3_003;

        Assert.Equal(new MediaTime(1001, 30_000), result);
    }

    [Fact]
    public void Division_preserves_exact_time()
    {
        Assert.Equal(new MediaTime(1, 10), new MediaTime(1, 1) / 10);
        Assert.Equal(new MediaTime(-1, 2), new MediaTime(1, 1) / -2);
        Assert.Throws<DivideByZeroException>(() => new MediaTime(1, 1) / 0);
    }
}
