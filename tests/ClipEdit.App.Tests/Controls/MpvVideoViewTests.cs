using Avalonia;
using ClipEdit.App.Controls;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using DomainPixelSize = ClipEdit.Domain.Geometry.PixelSize;

namespace ClipEdit.App.Tests.Controls;

public sealed class MpvVideoViewTests
{
    private static readonly MediaRange[] Ranges =
    [
        new MediaRange(new MediaTime(2, 1), new MediaTime(5, 1)),
        new MediaRange(new MediaTime(8, 1), new MediaTime(12, 1)),
    ];

    [Fact]
    public void Position_inside_kept_range_continues()
    {
        var decision = MpvVideoView.GetPlaybackRangeDecision(new MediaTime(3, 1), Ranges);

        Assert.Equal(PlaybackRangeAction.Continue, decision.Action);
        Assert.Null(decision.Target);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(5, 8)]
    [InlineData(7, 8)]
    public void Position_before_next_kept_range_seeks_to_its_start(int position, int expected)
    {
        var decision = MpvVideoView.GetPlaybackRangeDecision(new MediaTime(position, 1), Ranges);

        Assert.Equal(PlaybackRangeAction.Seek, decision.Action);
        Assert.Equal(new MediaTime(expected, 1), decision.Target);
    }

    [Fact]
    public void Position_at_final_end_completes_edited_playback()
    {
        var decision = MpvVideoView.GetPlaybackRangeDecision(new MediaTime(12, 1), Ranges);

        Assert.Equal(PlaybackRangeAction.End, decision.Action);
        Assert.Null(decision.Target);
    }

    [Fact]
    public void Identity_canvas_transform_preserves_libmpv_fit()
    {
        var transform = MpvVideoView.CalculatePreviewVideoTransform(
            new DomainPixelSize(1_920, 1_080),
            new DomainPixelSize(1_920, 1_080),
            ClipCanvasTransform.Identity,
            new Size(960, 540));
        Assert.Equal(1, transform.ScaleX);
        Assert.Equal(1, transform.ScaleY);

        Assert.Equal(1, transform.ZoomFactor, 6);
        Assert.Equal(0, transform.PanX);
        Assert.Equal(0, transform.PanY);
        Assert.Equal(0, transform.RotationDegrees);
    }

    [Fact]
    public void Canvas_offsets_and_rotation_lower_to_zoom_pan_and_rotation()
    {
        var transform = MpvVideoView.CalculatePreviewVideoTransform(
            new DomainPixelSize(1_920, 1_080),
            new DomainPixelSize(1_080, 1_080),
            new ClipCanvasTransform(100, -50, 1, 90),
            new Size(960, 540));

        Assert.Equal(16d / 9d, transform.ZoomFactor, 6);
        Assert.Equal(50d / 540d, transform.PanX, 6);
        Assert.Equal(-25d / 960d, transform.PanY, 6);
        Assert.Equal(90, transform.RotationDegrees);
    }

    [Fact]
    public void Non_uniform_canvas_scale_lowers_to_independent_mpv_display_scales()
    {
        var transform = MpvVideoView.CalculatePreviewVideoTransform(
            new DomainPixelSize(1_920, 1_080),
            new DomainPixelSize(1_920, 1_080),
            new ClipCanvasTransform(0, 0, 2, 0.5, 0),
            new Size(960, 540));

        Assert.Equal(1, transform.ZoomFactor, 6);
        Assert.Equal(2, transform.ScaleX, 6);
        Assert.Equal(0.5, transform.ScaleY, 6);
        Assert.Equal(0, transform.PanX);
        Assert.Equal(0, transform.PanY);
    }

}
