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
    public void Rendering_starts_before_media_load_completes()
    {
        Assert.True(MpvVideoView.CanRenderFrame(
            isEngineReady: true,
            isMediaLoaded: false));
        Assert.True(MpvVideoView.ShouldContinueRenderingDuringLoad(isLoadCompleted: false));
        Assert.False(MpvVideoView.ShouldContinueRenderingDuringLoad(isLoadCompleted: true));
    }

    [Fact]
    public void Becoming_effectively_visible_requests_a_fresh_render()
    {
        Assert.True(MpvVideoView.ShouldRequestRenderForViewport(new Rect(0, 0, 960, 540)));
        Assert.False(MpvVideoView.ShouldRequestRenderForViewport(default));
    }

    [Fact]
    public void Native_preview_viewport_tracks_monitor_render_scaling()
    {
        var physical = MpvVideoView.ScaleViewportForRender(new Size(960, 540), 1.5);

        Assert.Equal(new Size(1_440, 810), physical);
        Assert.Equal(
            new Size(960, 540),
            MpvVideoView.ScaleViewportForRender(new Size(960, 540), double.NaN));
    }

    [Fact]
    public void Source_framebuffer_downscales_to_the_preview_without_upscaling_small_media()
    {
        Assert.Equal(
            new DomainPixelSize(889, 500),
            MpvVideoView.CalculateSourceRenderSize(
                new DomainPixelSize(3_840, 2_160),
                new Size(1_200, 500)));
        Assert.Equal(
            new DomainPixelSize(640, 360),
            MpvVideoView.CalculateSourceRenderSize(
                new DomainPixelSize(640, 360),
                new Size(1_920, 1_080)));
    }

    [Fact]
    public void Identity_video_quad_fills_a_matching_canvas_inside_the_framebuffer()
    {
        var vertices = MpvVideoView.CalculateVideoQuadVertices(
            new DomainPixelSize(1_920, 1_080),
            new DomainPixelSize(1_920, 1_080),
            ClipCanvasTransform.Identity,
            new Size(960, 540));

        Assert.Equal(
            new float[]
            {
                -1, 1, 0, 1,
                1, 1, 1, 1,
                -1, -1, 0, 0,
                1, -1, 1, 0,
            },
            vertices);
    }

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
    public void Playback_start_preserves_a_playhead_inside_the_current_range()
    {
        var target = MpvVideoView.GetPlaybackStartPosition(new MediaTime(3, 1), Ranges, isEndOfFile: false);

        Assert.Equal(new MediaTime(3, 1), target);
    }

    [Fact]
    public void Playback_start_uses_the_next_kept_range_or_restarts_only_after_end()
    {
        Assert.Equal(
            new MediaTime(2, 1),
            MpvVideoView.GetPlaybackStartPosition(MediaTime.Zero, Ranges, isEndOfFile: false));
        Assert.Equal(
            new MediaTime(2, 1),
            MpvVideoView.GetPlaybackStartPosition(new MediaTime(10, 1), Ranges, isEndOfFile: true));
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

        Assert.Equal(0.5, transform.ZoomFactor, 6);
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

        Assert.Equal(0.5, transform.ZoomFactor, 6);
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

        Assert.Equal(0.5, transform.ZoomFactor, 6);
        Assert.Equal(2, transform.ScaleX, 6);
        Assert.Equal(0.5, transform.ScaleY, 6);
        Assert.Equal(0, transform.PanX);
        Assert.Equal(0, transform.PanY);
    }

    [Fact]
    public void Explicit_preview_scale_supports_large_sources_in_small_viewports()
    {
        var transform = MpvVideoView.CalculatePreviewVideoTransform(
            new DomainPixelSize(7_680, 4_320),
            new DomainPixelSize(7_680, 4_320),
            ClipCanvasTransform.Identity,
            new Size(38.4, 21.6));

        Assert.Equal(0.005, transform.ZoomFactor, 6);
    }

    [Fact]
    public void Rotated_non_uniform_scale_normalizes_pan_against_preview_order()
    {
        var sourceSize = new DomainPixelSize(1_920, 1_080);
        var canvasTransform = new ClipCanvasTransform(
            -266.49350649350606,
            -0.5844155844155807,
            0.44034090909090934,
            0.9090909090909091,
            17);

        var transform = MpvVideoView.CalculatePreviewVideoTransform(
            sourceSize,
            sourceSize,
            canvasTransform,
            new Size(960, 540));
        var radians = 17 * Math.PI / 180;
        var rotatedWidth = (sourceSize.Width * Math.Abs(Math.Cos(radians))) +
                           (sourceSize.Height * Math.Abs(Math.Sin(radians)));
        var rotatedHeight = (sourceSize.Width * Math.Abs(Math.Sin(radians))) +
                            (sourceSize.Height * Math.Abs(Math.Cos(radians)));

        Assert.Equal(canvasTransform.OffsetX / (rotatedWidth * canvasTransform.ScaleX), transform.PanX, 6);
        Assert.Equal(canvasTransform.OffsetY / (rotatedHeight * canvasTransform.ScaleY), transform.PanY, 6);
    }

    [Fact]
    public void Interactive_transform_flag_defaults_to_inactive()
    {
        var view = new MpvVideoView();

        Assert.False(view.IsInteractiveTransformActive);
    }

}
