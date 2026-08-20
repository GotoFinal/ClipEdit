using System.Collections.Immutable;
using ClipEdit.App.ViewModels;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.Tests.ViewModels;

public sealed class FastCutModeTests
{
    [Fact]
    public async Task Fast_mode_snaps_markers_and_split_to_indexed_keyframes()
    {
        var probe = new KeyframeProbe();
        using var viewModel = new MainWindowViewModel(probe);
        await viewModel.ImportFilesAsync([TestPath("source.mp4")]);

        viewModel.IsFastCutMode = true;
        Assert.True(viewModel.IsFastCutSnappingActive);
        Assert.Equal("Fast", viewModel.FastCutModeText);
        Assert.Equal(
            "Snap selection edges, clip trims, and Split to keyframes for faster exports.",
            viewModel.FastCutModeDetails);

        viewModel.SequencePlayheadSeconds = 4.8;
        viewModel.MarkSequenceSelectionStart();
        Assert.Equal(4, viewModel.SequenceSelectionStartSeconds, 6);

        Assert.True(viewModel.SplitSelectedVideoClip());
        Assert.Equal(2, viewModel.VideoClips.Count);
        Assert.Equal(4, viewModel.VideoClips[0].SourceEndSeconds, 6);
        Assert.Equal(4, viewModel.VideoClips[1].SourceStartSeconds, 6);
        Assert.Equal(4, viewModel.SequencePlayheadSeconds, 6);
    }

    [Fact]
    public async Task Exact_mode_keeps_frame_accurate_split_position()
    {
        var probe = new KeyframeProbe();
        using var viewModel = new MainWindowViewModel(probe);
        await viewModel.ImportFilesAsync([TestPath("source.mp4")]);

        Assert.Equal(
            "Keep frame-exact cuts. Turn on Fast for keyframe snapping.",
            viewModel.FastCutModeDetails);
        viewModel.SequencePlayheadSeconds = 4.8;

        Assert.True(viewModel.SplitSelectedVideoClip());
        Assert.Equal(4.8, viewModel.VideoClips[0].SourceEndSeconds, 6);
    }

    private static string TestPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);

    private sealed class KeyframeProbe : IMediaProbe, IKeyframeProbe
    {
        public Task<MediaProbeResult> ProbeAsync(
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = new MediaTime(10, 1);
            return Task.FromResult(new MediaProbeResult(
                sourcePath,
                "mov,mp4,m4a,3gp,3g2,mj2",
                "MP4",
                MediaTime.Zero,
                duration,
                1_000,
                1_000,
                ImmutableArray.Create<MediaStreamInfo>(new VideoStreamInfo(
                    0,
                    "h264",
                    null,
                    "High",
                    null,
                    null,
                    true,
                    false,
                    new MediaTime(1, 1_000),
                    MediaTime.Zero,
                    duration,
                    new PixelSize(1_920, 1_080),
                    0,
                    new FrameRate(30, 1),
                    new FrameRate(30, 1),
                    "yuv420p",
                    "1:1",
                    "16:9",
                    "tv",
                    "bt709",
                    "bt709",
                    "bt709",
                    "progressive"))));
        }

        public Task<KeyframeIndex> ProbeKeyframesAsync(
            string sourcePath,
            int videoStreamIndex,
            MediaTime timestampOrigin,
            MediaTime? sourceDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new KeyframeIndex(
                videoStreamIndex,
                [MediaTime.Zero, new MediaTime(2, 1), new MediaTime(4, 1), new MediaTime(6, 1), new MediaTime(8, 1)]));
        }
    }
}
