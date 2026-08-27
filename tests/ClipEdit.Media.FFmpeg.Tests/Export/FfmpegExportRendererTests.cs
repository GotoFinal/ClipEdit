using System.Collections.Immutable;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfmpegExportRendererTests
{
    [Fact]
    public void Remaining_time_prefers_ffmpeg_speed_and_falls_back_to_elapsed_progress()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            FfmpegExportRenderer.EstimateRemaining(
                TimeSpan.FromSeconds(100),
                TimeSpan.FromSeconds(40),
                reportedProcessingSpeed: 2,
                elapsed: TimeSpan.FromSeconds(100)));
        Assert.Equal(
            TimeSpan.FromSeconds(90),
            FfmpegExportRenderer.EstimateRemaining(
                TimeSpan.FromSeconds(100),
                TimeSpan.FromSeconds(40),
                reportedProcessingSpeed: null,
                elapsed: TimeSpan.FromSeconds(60)));
        Assert.Null(FfmpegExportRenderer.EstimateRemaining(
            TimeSpan.FromSeconds(100),
            TimeSpan.Zero,
            reportedProcessingSpeed: null,
            elapsed: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Hardware_fallback_recognizes_device_failures_but_not_unrelated_encode_errors()
    {
        Assert.True(FfmpegExportRenderer.IsHardwareAccelerationFailure(new ExportException(
            ExportFailure.ToolFailed,
            "FFmpeg export failed: Device creation failed: no capable devices found")));
        Assert.True(FfmpegExportRenderer.IsHardwareAccelerationFailure(new ExportException(
            ExportFailure.ToolFailed,
            "FFmpeg export failed: Error while opening encoder - maybe incorrect parameters")));
        Assert.False(FfmpegExportRenderer.IsHardwareAccelerationFailure(new ExportException(
            ExportFailure.ToolFailed,
            "FFmpeg export failed: No space left on device")));
        Assert.False(FfmpegExportRenderer.IsHardwareAccelerationFailure(new ExportException(
            ExportFailure.DestinationUnavailable,
            "No device available")));
    }

    [Theory]
    [InlineData(VideoCodecFamily.Hevc, "hevc")]
    [InlineData(VideoCodecFamily.Vp9, "vp9")]
    public void Incomplete_keyframe_copy_requires_a_bounded_decoder_validation(
        VideoCodecFamily codecFamily,
        string codecName)
    {
        var plan = CreateHevcKeyframeTrimPlan(
            TestPath("source.mkv"),
            TestPath("trimmed.mkv"),
            codecFamily,
            codecName);

        Assert.True(FfmpegExportRenderer.RequiresKeyframeCopyValidation(plan));

        var arguments = FfmpegExportRenderer.CreateKeyframeCopyValidationArguments(
            TestPath("candidate.mkv"),
            2);
        Assert.Contains("-xerror", arguments);
        Assert.Equal("2", ValueAfter(arguments, "-t"));
        Assert.Equal("0:v:0", ValueAfter(arguments, "-map"));
        Assert.Equal("null", ValueAfter(arguments, "-f"));
    }

    [Fact]
    public async Task Existing_destination_is_rejected_before_launch_and_remains_unchanged()
    {
        var sourcePath = Path.GetTempFileName();
        var destinationPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(destinationPath, "keep me");

        try
        {
            var renderer = new FfmpegExportRenderer(Environment.ProcessPath!);

            var exception = await Assert.ThrowsAsync<ExportException>(() =>
                renderer.RenderAsync(CreatePlan(sourcePath, destinationPath)));

            Assert.Equal(ExportFailure.DestinationExists, exception.Failure);
            Assert.Equal("keep me", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task Source_can_never_be_used_as_its_own_destination()
    {
        var sourcePath = Path.GetTempFileName();

        try
        {
            var renderer = new FfmpegExportRenderer(Environment.ProcessPath!);

            var exception = await Assert.ThrowsAsync<ExportException>(() =>
                renderer.RenderAsync(CreatePlan(sourcePath, sourcePath)));

            Assert.Equal(ExportFailure.DestinationUnavailable, exception.Failure);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task Authorized_replacement_swaps_only_after_the_new_output_exists()
    {
        var sourcePath = Path.GetTempFileName();
        var destinationPath = Path.GetTempFileName();
        var temporaryPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(destinationPath, "old output");
        await File.WriteAllTextAsync(temporaryPath, "new output");

        try
        {
            var plan = CreatePlan(sourcePath, destinationPath, replaceExistingDestination: true);

            FfmpegExportRenderer.FinalizeOutput(temporaryPath, plan);

            Assert.Equal("new output", await File.ReadAllTextAsync(destinationPath));
            Assert.False(File.Exists(temporaryPath));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(destinationPath)!,
                $".{Path.GetFileName(destinationPath)}.*.backup"));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
            File.Delete(temporaryPath);
        }
    }

    private static ExportPlan CreatePlan(
        string sourcePath,
        string destinationPath,
        bool replaceExistingDestination = false)
    {
        var preset = new ExportPreset(
            "mp4",
            "MP4",
            ".mp4",
            ExportContainer.Mp4,
            VideoCodecFamily.H264,
            AudioCodecFamily.Aac,
            requiresEvenDimensions: true);
        return new ExportPlan(
            sourcePath,
            destinationPath,
            0,
            null,
            CropRegion.FullFrame(new PixelSize(100, 100)),
            ImmutableArray.Create(new MediaRange(MediaTime.Zero, new MediaTime(1, 1))),
            preset,
            replaceExistingDestination);
    }

    private static ExportPlan CreateHevcKeyframeTrimPlan(
        string sourcePath,
        string destinationPath,
        VideoCodecFamily codecFamily = VideoCodecFamily.Hevc,
        string codecName = "hevc")
    {
        var canvas = new PixelSize(1_920, 1_080);
        var range = new MediaRange(new MediaTime(5, 1), new MediaTime(30, 1));
        var signature = new VideoStreamCopySignature(
            codecName,
            codecFamily == VideoCodecFamily.Hevc ? "hev1" : "vp09",
            "SHA256:video",
            canvas,
            new MediaTime(1, 1_000),
            new FrameRate(30, 1),
            "yuv420p10le",
            "Main 10",
            120,
            "1:1",
            "tv",
            "bt2020nc",
            "smpte2084",
            "bt2020",
            "progressive");
        var segment = new ExportVideoSegmentPlan(
            sourcePath,
            0,
            range,
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            timelineStart: range.Start,
            isCompleteSource: false,
            streamCopyInfo: new SegmentStreamCopyInfo(
                signature,
                null,
                true,
                true,
                new MediaTime(49, 10),
                new MediaTime(299, 10)));
        return new ExportPlan(
            [segment],
            canvas,
            destinationPath,
            new ExportPreset(
                "hevc-mkv",
                "HEVC MKV",
                ".mkv",
                ExportContainer.Matroska,
                codecFamily,
                AudioCodecFamily.None,
                requiresEvenDimensions: true),
            sequenceTimelineStart: range.Start,
            sequenceDuration: range.Duration,
            strategy: ExportStrategy.VideoStreamCopy);
    }

    private static string TestPath(string fileName) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), fileName));

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing argument {option}.");
        return arguments[index + 1];
    }
}
