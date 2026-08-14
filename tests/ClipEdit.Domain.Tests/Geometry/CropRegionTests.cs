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

    [Fact]
    public void MoveClamped_preserves_size_and_stays_in_source()
    {
        var crop = new CropRegion(new PixelSize(1_920, 1_080), 420, 0, 1_080, 1_080);

        var moved = crop.MoveClamped(2_000, -50);

        Assert.Equal(840, moved.X);
        Assert.Equal(0, moved.Y);
        Assert.Equal(crop.ExportSize, moved.ExportSize);
    }

    [Fact]
    public void FromEdges_uses_half_open_pixel_edges()
    {
        var crop = CropRegion.FromEdges(new PixelSize(1_920, 1_080), 100, 50, 1_100, 850);

        Assert.Equal(1_000, crop.Width);
        Assert.Equal(800, crop.Height);
        Assert.Equal(1_100, crop.Right);
        Assert.Equal(850, crop.Bottom);
    }

    [Fact]
    public void Source_quarter_turn_preserves_the_same_cropped_pixels()
    {
        var crop = new CropRegion(new PixelSize(1_920, 1_080), 100, 50, 1_000, 800);

        var rotated = crop.RotateSourceClockwise();

        Assert.Equal(new PixelSize(1_080, 1_920), rotated.SourceSize);
        Assert.Equal((230, 100, 800, 1_000),
            (rotated.X, rotated.Y, rotated.Width, rotated.Height));
    }

    [Theory]
    [InlineData(16, 9, 0, 0, 1_920, 1_080)]
    [InlineData(1, 1, 420, 0, 1_080, 1_080)]
    [InlineData(4, 5, 528, 0, 864, 1_080)]
    [InlineData(21, 9, 1, 129, 1_918, 822)]
    public void Aspect_ratio_resize_uses_the_largest_exact_centered_crop(
        int ratioWidth,
        int ratioHeight,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var crop = CropRegion.FullFrame(new PixelSize(1_920, 1_080));

        var resized = crop.ResizeToAspectRatio(ratioWidth, ratioHeight);

        Assert.Equal((expectedX, expectedY, expectedWidth, expectedHeight),
            (resized.X, resized.Y, resized.Width, resized.Height));
        Assert.Equal(
            (long)ratioWidth * resized.Height,
            (long)ratioHeight * resized.Width);
    }

    [Fact]
    public void Aspect_ratio_resize_preserves_the_current_crop_center_when_possible()
    {
        var crop = new CropRegion(new PixelSize(1_920, 1_080), 700, 300, 400, 300);

        var resized = crop.ResizeToAspectRatio(9, 16);

        Assert.Equal(599, resized.X);
        Assert.Equal(0, resized.Y);
        Assert.Equal(603, resized.Width);
        Assert.Equal(1_072, resized.Height);
    }

    [Fact]
    public void Aspect_ratio_resize_does_not_lock_subsequent_free_resizing()
    {
        var crop = CropRegion.FullFrame(new PixelSize(1_920, 1_080))
            .ResizeToAspectRatio(1, 1);

        var freelyResized = CropRegion.FromEdges(
            crop.SourceSize,
            crop.X,
            crop.Y,
            crop.Right + 100,
            crop.Bottom);

        Assert.NotEqual(freelyResized.Width, freelyResized.Height);
    }

    [Fact]
    public void Large_ratio_units_still_produce_a_close_crop_for_tiny_sources()
    {
        var crop = CropRegion.FullFrame(new PixelSize(100, 100));

        var resized = crop.ResizeToAspectRatio(239, 100);

        Assert.Equal(new PixelSize(100, 42), resized.ExportSize);
        Assert.Equal(2.38, resized.Width / (double)resized.Height, 2);
    }
}
