using ClipEdit.Domain.Geometry;

namespace ClipEdit.Domain.Tests.Geometry;

public sealed class ClipCanvasTransformTests
{
    [Fact]
    public void Fill_centers_source_and_covers_canvas()
    {
        var transform = ClipCanvasTransform.Fill(
            new PixelSize(1_920, 1_080),
            new PixelSize(1_080, 1_080));

        Assert.Equal(1, transform.Scale);
        Assert.Equal(0, transform.OffsetX);
        Assert.Equal(0, transform.OffsetY);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    public void Rotation_is_normalized(int requested, int expected)
    {
        var transform = new ClipCanvasTransform(12, -8, 1.5, requested);

        Assert.Equal(expected, transform.RotationDegrees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    public void Invalid_scale_is_rejected(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClipCanvasTransform(0, 0, scale, 0));
    }
}
