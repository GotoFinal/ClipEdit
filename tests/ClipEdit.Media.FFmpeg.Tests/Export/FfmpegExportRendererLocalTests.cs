using System.Collections.Immutable;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;
using ClipEdit.Media.FFmpeg.Probe;
using ClipEdit.Media.FFmpeg.Process;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfmpegExportRendererLocalTests
{
    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_preserves_hdr10_colorimetry_and_ten_bit_pixels()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_HDR_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var source = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(sourcePath);
        var video = source.VideoStreams.First();
        var colorInfo = new ExportVideoColorInfo(
            video.PixelFormat,
            video.ColorRange,
            video.ColorSpace,
            video.ColorTransfer,
            video.ColorPrimaries);
        if (!colorInfo.CanPreserveHdr)
        {
            return;
        }

        var outputSize = new PixelSize(
            Math.Min(video.OrientedSize.Width, 640) & ~1,
            Math.Min(video.OrientedSize.Height, 480) & ~1);
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-hdr-{Guid.NewGuid():N}.mp4");
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    sourcePath,
                    video.Index,
                    new MediaRange(MediaTime.Zero, new MediaTime(1, 1)),
                    video.OrientedSize,
                    CropRegion.FullFrame(video.OrientedSize),
                    new ClipCanvasTransform(0, 0, 0.9, 0.9, 7),
                    videoColorInfo: colorInfo),
            ],
            outputSize,
            destinationPath,
            new ExportPreset(
                "mp4-local-hdr",
                "MP4 local HDR",
                ".mp4",
                ExportContainer.Mp4,
                VideoCodecFamily.H264,
                AudioCodecFamily.None,
                requiresEvenDimensions: true));

        try
        {
            await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);
            var rendered = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(destinationPath);
            var output = rendered.VideoStreams.Single();

            Assert.Contains("10", output.PixelFormat, StringComparison.Ordinal);
            Assert.Equal(video.ColorRange, output.ColorRange);
            Assert.Equal(video.ColorSpace, output.ColorSpace);
            Assert.Equal(video.ColorTransfer, output.ColorTransfer);
            Assert.Equal(video.ColorPrimaries, output.ColorPrimaries);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_applies_clip_and_global_speed_to_video_and_audio()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var probe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(sourcePath);
        if (probe.Duration is null || probe.Duration < new MediaTime(2, 1))
        {
            return;
        }

        var video = probe.VideoStreams.First();
        var outputSize = new PixelSize(
            Math.Min(video.OrientedSize.Width, 320),
            Math.Min(video.OrientedSize.Height, 180));
        var crop = new CropRegion(
            video.OrientedSize,
            0,
            0,
            outputSize.Width,
            outputSize.Height);
        var audioTracks = probe.AudioStreams.FirstOrDefault() is { } audio
            ? ImmutableArray.Create(new ExportAudioTrackPlan(audio.Index, 0))
            : ImmutableArray<ExportAudioTrackPlan>.Empty;
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-speed-{Guid.NewGuid():N}.mp4");
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    sourcePath,
                    video.Index,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    video.OrientedSize,
                    crop,
                    ClipCanvasTransform.Identity,
                    audioTracks,
                    MediaTime.Zero,
                    playbackSpeedPercent: 150),
            ],
            outputSize,
            destinationPath,
            new ExportPreset(
                "mp4-local-speed",
                "MP4 local speed",
                ".mp4",
                ExportContainer.Mp4,
                VideoCodecFamily.H264,
                AudioCodecFamily.Aac,
                requiresEvenDimensions: true),
            encodingSettings: new ExportEncodingSettings(playbackSpeedPercent: 200));

        try
        {
            var result = await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);
            var rendered = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(result.DestinationPath);

            Assert.True(result.FileSizeBytes > 0);
            Assert.InRange(rendered.Duration!.Value.TotalSeconds, 0.5, 0.9);
            if (!audioTracks.IsEmpty)
            {
                Assert.NotEmpty(rendered.AudioStreams);
            }
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_exports_a_scaled_palette_gif_from_the_opt_in_sample()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var probe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(sourcePath);
        var video = probe.VideoStreams.First();
        var cropSize = new PixelSize(
            Math.Min(video.OrientedSize.Width, 320),
            Math.Min(video.OrientedSize.Height, 180));
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-gif-{Guid.NewGuid():N}.gif");
        var plan = new ExportPlan(
            sourcePath,
            destinationPath,
            video.Index,
            audioStreamIndex: null,
            new CropRegion(video.OrientedSize, 0, 0, cropSize.Width, cropSize.Height),
            [new MediaRange(MediaTime.Zero, new MediaTime(1, 1))],
            new ExportPreset(
                "gif-local",
                "GIF local",
                ".gif",
                ExportContainer.Gif,
                VideoCodecFamily.Gif,
                AudioCodecFamily.None,
                requiresEvenDimensions: false),
            encodingSettings: new ExportEncodingSettings(50, 50, 10));

        try
        {
            var result = await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);
            var rendered = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(result.DestinationPath);

            Assert.True(result.FileSizeBytes > 0);
            Assert.Equal(plan.OutputSize, rendered.VideoStreams.Single().OrientedSize);
            Assert.Empty(rendered.AudioStreams);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_pads_and_mixes_external_audio_that_ends_before_the_video()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var externalAudioPath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_EXTERNAL_AUDIO");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(externalAudioPath) ||
            !File.Exists(sourcePath) ||
            !File.Exists(externalAudioPath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var probe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(sourcePath);
        var video = probe.VideoStreams.First();
        var embeddedAudio = probe.AudioStreams.First();
        var externalProbe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(externalAudioPath);
        var externalAudio = externalProbe.AudioStreams.First();
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-external-audio-{Guid.NewGuid():N}.mp4");
        var plan = new ExportPlan(
            sourcePath,
            destinationPath,
            video.Index,
            audioStreamIndex: null,
            new CropRegion(video.OrientedSize, 0, 0, 320, 180),
            [new MediaRange(MediaTime.Zero, new MediaTime(3, 1))],
            new ExportPreset(
                "mp4-local-external-audio",
                "MP4 local external audio",
                ".mp4",
                ExportContainer.Mp4,
                VideoCodecFamily.H264,
                AudioCodecFamily.Aac,
                requiresEvenDimensions: true),
            audioTracks:
            [
                new ExportAudioTrackPlan(embeddedAudio.Index, -3),
                new ExportAudioTrackPlan(
                    externalAudioPath,
                    externalAudio.Index,
                    -12,
                    new MediaTime(1, 2),
                    new SourceEdit(externalProbe.Duration!.Value).Remove(
                        new MediaRange(new MediaTime(1, 4), new MediaTime(3, 4)))),
            ]);

        try
        {
            var result = await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);
            var rendered = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(result.DestinationPath);

            Assert.True(result.FileSizeBytes > 0);
            Assert.NotEmpty(rendered.AudioStreams);
            Assert.True(rendered.Duration >= new MediaTime(29, 10));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_exports_a_short_cropped_av_clip_from_the_opt_in_sample()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var probe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(sourcePath);
        var video = probe.VideoStreams.First();
        var audio = probe.AudioStreams.FirstOrDefault();
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-export-{Guid.NewGuid():N}.mp4");
        var preset = new ExportPreset(
            "mp4-local-test",
            "MP4 local test",
            ".mp4",
            ExportContainer.Mp4,
            VideoCodecFamily.H264,
            AudioCodecFamily.Aac,
            requiresEvenDimensions: true);
        var plan = new ExportPlan(
            sourcePath,
            destinationPath,
            video.Index,
            audio?.Index,
            new CropRegion(video.OrientedSize, 0, 0, 320, 180),
            ImmutableArray.Create(
                new MediaRange(new MediaTime(1, 1), new MediaTime(2, 1)),
                new MediaRange(new MediaTime(3, 1), new MediaTime(4, 1))),
            preset);

        try
        {
            var result = await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);

            Assert.Equal(destinationPath, result.DestinationPath);
            Assert.True(result.FileSizeBytes > 0);
            Assert.True(File.Exists(destinationPath));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_concatenates_mirrored_clip_transforms_beneath_one_shared_crop()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var probe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(sourcePath);
        var video = probe.VideoStreams.First();
        var audio = probe.AudioStreams.FirstOrDefault();
        var audioPlans = audio is null
            ? ImmutableArray<ExportAudioTrackPlan>.Empty
            : [new ExportAudioTrackPlan(audio.Index, -3)];
        var canvasSize = video.OrientedSize;
        var sharedCrop = new CropRegion(
            canvasSize,
            (canvasSize.Width - 640) / 2,
            (canvasSize.Height - 360) / 2,
            640,
            360);
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-sequence-{Guid.NewGuid():N}.mp4");
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    sourcePath,
                    video.Index,
                    new MediaRange(new MediaTime(1, 1), new MediaTime(2, 1)),
                    canvasSize,
                    sharedCrop,
                    new ClipCanvasTransform(
                        -160,
                        0,
                        1.1,
                        0,
                        isHorizontallyMirrored: true),
                    audioPlans),
                new ExportVideoSegmentPlan(
                    sourcePath,
                    video.Index,
                    new MediaRange(new MediaTime(3, 1), new MediaTime(4, 1)),
                    canvasSize,
                    sharedCrop,
                    new ClipCanvasTransform(
                        160,
                        0,
                        0.5,
                        1.1,
                        15,
                        isVerticallyMirrored: true),
                    audioPlans),
            ],
            new PixelSize(320, 180),
            destinationPath,
            new ExportPreset(
                "mp4-local-sequence",
                "MP4 local sequence",
                ".mp4",
                ExportContainer.Mp4,
                VideoCodecFamily.H264,
                AudioCodecFamily.Aac,
                requiresEvenDimensions: true));

        try
        {
            var result = await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);
            var rendered = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(result.DestinationPath);

            Assert.True(result.FileSizeBytes > 0);
            Assert.Equal(new PixelSize(320, 180), rendered.VideoStreams.Single().OrientedSize);
            Assert.True(rendered.Duration >= new MediaTime(19, 10));
            Assert.True(rendered.Duration <= new MediaTime(21, 10));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_packet_copies_keyframe_trimmed_h264_vp9_or_av1_video_and_rebuilds_audio()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_KEYFRAME_COPY_MEDIA") ??
                         Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var mediaProbe = new FfprobeMediaProbe(ffprobePath);
        var probe = await mediaProbe.ProbeAsync(sourcePath);
        var video = probe.VideoStreams.FirstOrDefault();
        var sourceDuration = video?.Duration ?? probe.Duration;
        if (video is null ||
            video.CodecName is not ("h264" or "vp9" or "av1") ||
            video.RotationDegrees != 0 ||
            sourceDuration is null ||
            video.TimeBase is not { } timeBase ||
            video.AverageFrameRate is not { IsZero: false } frameRate ||
            string.IsNullOrWhiteSpace(video.PixelFormat))
        {
            return;
        }

        var keyframes = await mediaProbe.ProbeKeyframesAsync(
            sourcePath,
            video.Index,
            video.StartTime ?? probe.StartTime,
            sourceDuration);
        var boundaries = keyframes.Points
            .Where(static point => point.DecodeTimestamp is not null)
            .Take(3)
            .ToArray();
        if (boundaries.Length < 3 || boundaries[2].DecodeTimestamp <= boundaries[1].PresentationTimestamp)
        {
            return;
        }

        var range = new MediaRange(
            boundaries[1].PresentationTimestamp,
            boundaries[2].PresentationTimestamp);
        var audioPlans = probe.AudioStreams.FirstOrDefault() is { } audio
            ? ImmutableArray.Create(new ExportAudioTrackPlan(
                audio.Index,
                0,
                new SourceEdit(sourceDuration.Value)))
            : ImmutableArray<ExportAudioTrackPlan>.Empty;
        var signature = string.IsNullOrWhiteSpace(video.CodecTag) ||
                        string.IsNullOrWhiteSpace(video.CodecExtradataHash)
            ? null
            : new VideoStreamCopySignature(
                video.CodecName,
                video.CodecTag,
                video.CodecExtradataHash,
                video.EncodedSize,
                timeBase,
                frameRate,
                video.PixelFormat,
                video.Profile,
                video.CodecLevel,
                video.SampleAspectRatio,
                video.ColorRange,
                video.ColorSpace,
                video.ColorTransfer,
                video.ColorPrimaries,
                video.FieldOrder);
        var isH264 = video.CodecName == "h264";
        var usesMp4 = isH264 || Path.GetExtension(sourcePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        var extension = usesMp4 ? ".mp4" : ".webm";
        var container = usesMp4 ? ExportContainer.Mp4 : ExportContainer.WebM;
        var videoCodec = video.CodecName == "h264"
            ? VideoCodecFamily.H264
            : video.CodecName == "vp9"
                ? VideoCodecFamily.Vp9
                : VideoCodecFamily.Av1;
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-keyframe-copy-{Guid.NewGuid():N}{extension}");
        var segment = new ExportVideoSegmentPlan(
            sourcePath,
            video.Index,
            range,
            video.EncodedSize,
            CropRegion.FullFrame(video.EncodedSize),
            ClipCanvasTransform.Identity,
            audioPlans,
            range.Start,
            isCompleteSource: false,
            sourceSize: video.EncodedSize,
            streamCopyInfo: new SegmentStreamCopyInfo(
                signature,
                null,
                true,
                true,
                boundaries[1].DecodeTimestamp,
                boundaries[2].DecodeTimestamp));
        var plan = new ExportPlan(
            [segment],
            video.EncodedSize,
            destinationPath,
            new ExportPreset(
                "local-keyframe-copy",
                "Local keyframe copy",
                extension,
                container,
                videoCodec,
                audioPlans.IsEmpty
                    ? AudioCodecFamily.None
                    : usesMp4 ? AudioCodecFamily.Aac : AudioCodecFamily.Opus,
                requiresEvenDimensions: true),
            sequenceTimelineStart: range.Start,
            sequenceDuration: range.Duration,
            strategy: ExportStrategy.VideoStreamCopy);

        try
        {
            await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);
            var rendered = await mediaProbe.ProbeAsync(destinationPath);
            var renderedVideo = Assert.Single(rendered.VideoStreams);
            var tolerance = new MediaTime(1, 10);

            Assert.Equal(video.CodecName, renderedVideo.CodecName, ignoreCase: true);
            var renderedDuration = renderedVideo.Duration ?? rendered.Duration ?? MediaTime.Zero;
            Assert.True(renderedDuration >= range.Duration - tolerance);
            Assert.True(renderedDuration <= range.Duration + tolerance);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_validates_boundary_gop_h264_vp9_or_av1_splices_before_finalizing()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_BOUNDARY_GOP_MEDIA") ??
                         Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var mediaProbe = new FfprobeMediaProbe(ffprobePath);
        var probe = await mediaProbe.ProbeAsync(sourcePath);
        var video = probe.VideoStreams.FirstOrDefault();
        var sourceDuration = video?.Duration ?? probe.Duration;
        if (video is null ||
            sourceDuration is null ||
            video.CodecName is not ("h264" or "vp9" or "av1") ||
            !string.Equals(video.PixelFormat, "yuv420p", StringComparison.OrdinalIgnoreCase) ||
            video.RotationDegrees != 0 ||
            video.TimeBase is not { } timeBase ||
            video.AverageFrameRate is not { IsZero: false } frameRate ||
            video.NominalFrameRate != frameRate)
        {
            return;
        }

        var keyframes = await mediaProbe.ProbeKeyframesAsync(
            sourcePath,
            video.Index,
            video.StartTime ?? probe.StartTime,
            sourceDuration);
        var boundaries = keyframes.Points
            .Where(static point => point.DecodeTimestamp is not null)
            .Skip(1)
            .Take(3)
            .ToArray();
        if (boundaries.Length < 3 || frameRate.Numerator > int.MaxValue)
        {
            return;
        }

        var frameDuration = new MediaTime(frameRate.Denominator, (int)frameRate.Numerator);
        var range = new MediaRange(
            boundaries[0].PresentationTimestamp - frameDuration,
            boundaries[2].PresentationTimestamp + frameDuration);
        if (range.Start < MediaTime.Zero || range.End > sourceDuration.Value)
        {
            return;
        }

        var boundaryVideo = new BoundaryGopVideoInfo(
            video.CodecName,
            video.EncodedSize,
            timeBase,
            frameRate,
            video.PixelFormat!);
        var boundary = new BoundaryGopRenderInfo(
            boundaryVideo,
            range,
            boundaries[0].PresentationTimestamp,
            boundaries[0].DecodeTimestamp!.Value,
            boundaries[2].PresentationTimestamp,
            boundaries[2].DecodeTimestamp!.Value);
        var audioPlans = probe.AudioStreams.FirstOrDefault() is { } audio
            ? ImmutableArray.Create(new ExportAudioTrackPlan(
                audio.Index,
                0,
                new SourceEdit(sourceDuration.Value)))
            : ImmutableArray<ExportAudioTrackPlan>.Empty;
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-boundary-gop-{Guid.NewGuid():N}{(video.CodecName == "h264" ? ".mp4" : ".webm")}");
        var segment = new ExportVideoSegmentPlan(
            sourcePath,
            video.Index,
            range,
            video.EncodedSize,
            CropRegion.FullFrame(video.EncodedSize),
            ClipCanvasTransform.Identity,
            audioPlans,
            range.Start,
            sourceSize: video.EncodedSize,
            boundaryGopInfo: boundary);
        var plan = new ExportPlan(
            [segment],
            video.EncodedSize,
            destinationPath,
            new ExportPreset(
                "local-boundary-gop",
                "Local Boundary-GOP",
                video.CodecName == "h264" ? ".mp4" : ".webm",
                video.CodecName == "h264" ? ExportContainer.Mp4 : ExportContainer.WebM,
                video.CodecName == "h264"
                    ? VideoCodecFamily.H264
                    : video.CodecName == "vp9" ? VideoCodecFamily.Vp9 : VideoCodecFamily.Av1,
                audioPlans.IsEmpty
                    ? AudioCodecFamily.None
                    : video.CodecName == "h264" ? AudioCodecFamily.Aac : AudioCodecFamily.Opus,
                requiresEvenDimensions: true,
                frameRate: frameRate,
                videoBitRateBitsPerSecond: video.BitRateBitsPerSecond),
            sequenceTimelineStart: range.Start,
            sequenceDuration: range.Duration,
            strategy: ExportStrategy.BoundaryGop);
        var phases = new List<string>();
        var progress = new InlineProgress(update => phases.Add(update.Phase));

        try
        {
            await new FfmpegExportRenderer(ffmpegPath, ffprobePath).RenderAsync(plan, progress);
            var rendered = await mediaProbe.ProbeAsync(destinationPath);

            Assert.Equal(video.CodecName, Assert.Single(rendered.VideoStreams).CodecName, ignoreCase: true);
            Assert.Contains(phases, phase => phase.Contains("Boundary-GOP validated", StringComparison.Ordinal));
            Assert.DoesNotContain(phases, phase => phase.Contains("encoding exactly", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    private sealed class InlineProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
