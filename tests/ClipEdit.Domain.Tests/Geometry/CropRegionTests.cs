using ClipEdit.Domain.Geometry;

namespace ClipEdit.Domain.Tests.Geometry;

public sealed class CropRegionTests
{
    [Fact]
    public void Default_value_is_a_valid_one_pixel_full_frame_crop()
    {
        CropRegion crop = default;

        Assert.Equal(new PixelSize(1, 1), crop.SourceSize);
        Assert.Equal(new PixelSize(1, 1), crop.ExportSize);
        Assert.True(crop.IsFullFrame);
    }

    [Fact]
    public void Full_frame_uses_the_entire_oriented_source()
    {
        var sourceSize = new PixelSize(1_920, 1_080);

        var crop = CropRegion.FullFrame(sourceSize);

        Assert.True(crop.IsFullFrame);
        Assert.Equal(sourceSize, crop.ExportSize);
    }

    [Fact]
    public void Export_size_matches_the_selected_crop()
    {
        var crop = new CropRegion(new PixelSize(1_920, 1_080), 420, 0, 1_080, 1_080);

        Assert.Equal(new PixelSize(1_080, 1_080), crop.ExportSize);
        Assert.False(crop.IsFullFrame);
    }

    [Theory]
    [InlineData(-1, 0, 100, 100)]
    [InlineData(0, -1, 100, 100)]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, 0, 100, 0)]
    [InlineData(1_900, 0, 100, 100)]
    [InlineData(0, 1_000, 100, 100)]
    public void Constructor_rejects_invalid_or_out_of_bounds_regions(
        int x,
        int y,
        int width,
        int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CropRegion(new PixelSize(1_920, 1_080), x, y, width, height));
    }
}
