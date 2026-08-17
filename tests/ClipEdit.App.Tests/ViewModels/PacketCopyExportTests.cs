using System.Collections.Immutable;
using ClipEdit.App.ViewModels;
using ClipEdit.Application.Export;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.Tests.ViewModels;

public sealed class PacketCopyExportTests
{
    [Fact]
    public async Task Match_input_format_preserves_source_timing_and_uses_packet_copy()
    {
        var renderer = new RecordingExportRenderer();
        using var viewModel = new MainWindowViewModel(
            new CompatibleCopyProbe(),
            exportRenderer: renderer);
        await viewModel.ImportFilesAsync([TestPath("source.mp4")]);

        viewModel.SelectedExportPreset = BuiltInExportPresets.MatchInput;

        Assert.True(viewModel.IsPacketCopyExport);
        Assert.Equal("Fast packet copy", viewModel.ExportMethodTitle);
        Assert.Contains("without filters or quality loss", viewModel.ExportMethodDetails);
        Assert.Contains("packet copy · no re-encode", viewModel.ExportPlanSummary);

        var result = await viewModel.ExportAsync(
            TestPath("copied.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(ExportStrategy.StreamCopy, renderer.Plan!.Strategy);
        Assert.NotNull(renderer.Plan.Preset.FrameRate);
    }

    [Fact]
    public async Task Reencode_reasons_identify_export_settings_and_source_settings_fix_them()
    {
        using var viewModel = new MainWindowViewModel(new CompatibleCopyProbe());
        await viewModel.ImportFilesAsync([TestPath("source.mp4")]);
        viewModel.SelectedExportPreset = BuiltInExportPresets.WebM;
        viewModel.SelectedExportQuality = ExportQualityChoice.Custom;
        viewModel.ExportScalePercent = 75;
        viewModel.ExportPlaybackSpeedPercent = 125;

        Assert.False(viewModel.IsPacketCopyExport);
        Assert.Equal("Full re-encode", viewModel.ExportMethodTitle);
        Assert.Contains("Quality is set to Custom", viewModel.ExportMethodDetails);
        Assert.Contains("Export scale is not 100%", viewModel.ExportMethodDetails);
        Assert.Contains("Export playback speed is not 100%", viewModel.ExportMethodDetails);
        Assert.Contains("video codec differs", viewModel.ExportMethodDetails);
        Assert.True(viewModel.CanApplyFastCopySettings);

        Assert.True(viewModel.ApplyFastCopySettings());

        Assert.Same(BuiltInExportPresets.MatchInput, viewModel.SelectedExportPreset);
        Assert.Equal(ExportQualityMode.MatchSource, viewModel.ExportQualityMode);
        Assert.Equal(100, viewModel.ExportScalePercent);
        Assert.Equal(100, viewModel.ExportPlaybackSpeedPercent);
        Assert.True(viewModel.IsPacketCopyExport);
    }

    [Fact]
    public async Task Exact_selection_of_one_complete_clip_can_be_copied_from_a_larger_sequence()
    {
        using var viewModel = new MainWindowViewModel(new CompatibleCopyProbe());
        await viewModel.ImportFilesAsync([TestPath("first.mp4"), TestPath("second.mp4")]);

        viewModel.SequenceSelectionStartSeconds = 0;
        viewModel.SequenceSelectionEndSeconds = 60;

        Assert.True(viewModel.IsPacketCopyExport);
        Assert.Contains("packet copy · no re-encode", viewModel.ExportPlanSummary);

        viewModel.SequenceSelectionStartSeconds = 5;
        viewModel.SequenceSelectionEndSeconds = 30;

        Assert.False(viewModel.IsPacketCopyExport);
        Assert.Contains("Trimmed clips still require encoding", viewModel.ExportMethodDetails);
    }

    [Fact]
    public async Task Audio_only_edits_copy_video_and_encode_only_audio()
    {
        var renderer = new RecordingExportRenderer();
        using var viewModel = new MainWindowViewModel(
            new CompatibleCopyProbe(),
            exportRenderer: renderer);
        await viewModel.ImportFilesAsync([TestPath("source.mp4")]);

        Assert.Single(viewModel.AudioTracks).GainDb = -3;

        Assert.False(viewModel.IsPacketCopyExport);
        Assert.True(viewModel.IsVideoStreamCopyExport);
        Assert.False(viewModel.IsFullReencodeExport);
        Assert.Equal("Fast video copy", viewModel.ExportMethodTitle);
        Assert.Contains("only audio will be processed", viewModel.ExportMethodDetails);
        Assert.Contains("video copy · audio re-encode", viewModel.ExportPlanSummary);

        var result = await viewModel.ExportAsync(
            TestPath("audio-adjusted.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(ExportStrategy.VideoStreamCopy, renderer.Plan!.Strategy);
    }

    [Fact]
    public async Task Keyframe_aligned_trim_copies_video_and_rebuilds_audio()
    {
        var renderer = new RecordingExportRenderer();
        using var viewModel = new MainWindowViewModel(
            new CompatibleCopyProbe(includePacketTimestamps: true),
            exportRenderer: renderer);
        await viewModel.ImportFilesAsync([TestPath("source.mp4")]);
        viewModel.SelectedExportPreset = BuiltInExportPresets.MatchInput;
        viewModel.SequenceSelectionStartSeconds = 5;
        viewModel.SequenceSelectionEndSeconds = 30;
        Assert.Single(viewModel.AudioTracks).GainDb = -3;

        Assert.False(viewModel.IsPacketCopyExport);
        Assert.True(viewModel.IsVideoStreamCopyExport);
        Assert.Equal("Fast video copy", viewModel.ExportMethodTitle);

        var result = await viewModel.ExportAsync(
            TestPath("keyframe-trim.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(ExportStrategy.VideoStreamCopy, renderer.Plan!.Strategy);
        var segment = Assert.Single(renderer.Plan.VideoSegments);
        Assert.Equal(new MediaRange(new MediaTime(5, 1), new MediaTime(30, 1)), segment.SourceRange);
        Assert.Equal(-3, Assert.Single(segment.AudioTracks).GainDb);
        Assert.Equal(new MediaTime(49, 10), segment.StreamCopyInfo!.StartDecodeTimestamp);
        Assert.Equal(new MediaTime(299, 10), segment.StreamCopyInfo.EndDecodeTimestamp);
    }

    [Fact]
    public async Task Experimental_boundary_gop_uses_complete_interior_gops_for_an_exact_trim()
    {
        var renderer = new RecordingExportRenderer();
        using var viewModel = new MainWindowViewModel(
            new CompatibleCopyProbe(includePacketTimestamps: true),
            exportRenderer: renderer);
        await viewModel.ImportFilesAsync([TestPath("source.mp4")]);
        viewModel.SelectedExportPreset = BuiltInExportPresets.MatchInput;
        viewModel.EnableExperimentalBoundaryGopRendering = true;
        viewModel.SequenceSelectionStartSeconds = 6;
        viewModel.SequenceSelectionEndSeconds = 29;

        Assert.True(viewModel.IsBoundaryGopExport);
        Assert.False(viewModel.IsFullReencodeExport);
        Assert.Equal("Experimental Boundary-GOP", viewModel.ExportMethodTitle);
        Assert.Contains("falls back to a full exact encode", viewModel.ExportMethodDetails);

        var result = await viewModel.ExportAsync(
            TestPath("boundary-gop.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(ExportStrategy.BoundaryGop, renderer.Plan!.Strategy);
        var boundary = Assert.Single(renderer.Plan.VideoSegments).BoundaryGopInfo;
        Assert.NotNull(boundary);
        Assert.Equal(new MediaTime(10, 1), boundary.CopiedStartPresentationTimestamp);
        Assert.Equal(new MediaTime(20, 1), boundary.CopiedEndPresentationTimestamp);
    }

    [Fact]
    public async Task Boundary_gop_fallback_is_reported_after_a_successful_exact_export()
    {
        var renderer = new RecordingExportRenderer(ExportStrategy.ExactTranscode);
        using var viewModel = new MainWindowViewModel(
            new CompatibleCopyProbe(includePacketTimestamps: true),
            exportRenderer: renderer);
        await viewModel.ImportFilesAsync([TestPath("source.mp4")]);
        viewModel.SelectedExportPreset = BuiltInExportPresets.MatchInput;
        viewModel.EnableExperimentalBoundaryGopRendering = true;
        viewModel.SequenceSelectionStartSeconds = 6;
        viewModel.SequenceSelectionEndSeconds = 29;

        var result = await viewModel.ExportAsync(
            TestPath("boundary-fallback.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Contains("exact fallback", viewModel.ExportPhaseText, StringComparison.Ordinal);
        Assert.Contains("validation failed", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webm_vp9_opus_source_uses_packet_copy()
    {
        using var viewModel = new MainWindowViewModel(
            new CompatibleCopyProbe("vp9", "opus", "webm"));
        await viewModel.ImportFilesAsync([TestPath("source.webm")]);

        viewModel.SelectedExportPreset = BuiltInExportPresets.WebM;

        Assert.True(viewModel.IsPacketCopyExport);
        Assert.Contains("packet copy · no re-encode", viewModel.ExportPlanSummary);
    }

    [Fact]
    public async Task Compatible_complete_clips_use_packet_copy_concatenation()
    {
        var renderer = new RecordingExportRenderer();
        using var viewModel = new MainWindowViewModel(
            new CompatibleCopyProbe(),
            exportRenderer: renderer);
        await viewModel.ImportFilesAsync([TestPath("first.mp4"), TestPath("second.mp4")]);
        viewModel.SelectedExportPreset = BuiltInExportPresets.MatchInput;

        Assert.True(viewModel.IsPacketCopyExport);
        Assert.Equal("Fast packet copy", viewModel.ExportMethodTitle);
        Assert.Contains("joined without decoding", viewModel.ExportMethodDetails);
        Assert.Contains("packet-copy join · no re-encode", viewModel.ExportPlanSummary);

        var result = await viewModel.ExportAsync(
            TestPath("joined.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(ExportStrategy.ConcatStreamCopy, renderer.Plan!.Strategy);
        Assert.Equal(2, renderer.Plan.VideoSegments.Length);
    }

    private static string TestPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);

    private sealed class CompatibleCopyProbe : IMediaProbe, IKeyframeProbe
    {
        private readonly string _videoCodec;
        private readonly string _audioCodec;
        private readonly string _formatName;
        private readonly bool _includePacketTimestamps;

        public CompatibleCopyProbe(
            string videoCodec = "h264",
            string audioCodec = "aac",
            string formatName = "mov,mp4,m4a,3gp,3g2,mj2",
            bool includePacketTimestamps = false)
        {
            _videoCodec = videoCodec;
            _audioCodec = audioCodec;
            _formatName = formatName;
            _includePacketTimestamps = includePacketTimestamps;
        }

        public Task<MediaProbeResult> ProbeAsync(
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = new MediaTime(60, 1);
            return Task.FromResult(new MediaProbeResult(
                sourcePath,
                _formatName,
                _formatName,
                MediaTime.Zero,
                duration,
                48_000_000,
                6_400_000,
                ImmutableArray.Create<MediaStreamInfo>(
                    new VideoStreamInfo(
                        0,
                        _videoCodec,
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
                        "progressive",
                        6_208_000,
                        _videoCodec == "h264" ? "avc1" : "[0][0][0][0]",
                        _videoCodec == "h264" ? 40 : null,
                        "SHA256:compatible-video"),
                    new AudioStreamInfo(
                        1,
                        _audioCodec,
                        null,
                        null,
                        null,
                        null,
                        true,
                        false,
                        new MediaTime(1, 48_000),
                        MediaTime.Zero,
                        duration,
                        48_000,
                        2,
                        "stereo",
                        "fltp",
                        192_000,
                        _audioCodec == "aac" ? "mp4a" : "[0][0][0][0]",
                        "SHA256:compatible-audio"))));
        }

        public Task<KeyframeIndex> ProbeKeyframesAsync(
            string sourcePath,
            int videoStreamIndex,
            MediaTime timestampOrigin,
            MediaTime? sourceDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_includePacketTimestamps
                ? KeyframeIndex.FromPoints(
                    videoStreamIndex,
                    [
                        new KeyframePoint(MediaTime.Zero, new MediaTime(-1, 10)),
                        new KeyframePoint(new MediaTime(5, 1), new MediaTime(49, 10)),
                        new KeyframePoint(new MediaTime(10, 1), new MediaTime(99, 10)),
                        new KeyframePoint(new MediaTime(20, 1), new MediaTime(199, 10)),
                        new KeyframePoint(new MediaTime(30, 1), new MediaTime(299, 10)),
                    ])
                : new KeyframeIndex(videoStreamIndex, ImmutableArray<MediaTime>.Empty));
        }
    }

    private sealed class RecordingExportRenderer(ExportStrategy? actualStrategy = null) : IExportRenderer
    {
        public ExportPlan? Plan { get; private set; }

        public Task<ExportResult> RenderAsync(
            ExportPlan plan,
            IProgress<ExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plan = plan;
            return Task.FromResult(new ExportResult(
                plan.DestinationPath,
                1_024,
                TimeSpan.FromMilliseconds(10),
                actualStrategy));
        }
    }
}
