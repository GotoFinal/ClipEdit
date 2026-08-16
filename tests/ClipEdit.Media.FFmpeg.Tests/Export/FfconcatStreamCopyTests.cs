using System.Collections.Immutable;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfconcatStreamCopyTests
{
    [Fact]
    public void Compatible_complete_segments_lower_to_a_safe_concat_manifest_and_copy_arguments()
    {
        var plan = CreatePlan(CreateSignature("SHA256:video"), CreateSignature("SHA256:video"));
        var manifestPath = TestPath("join.ffconcat");

        var manifest = FfconcatManifest.Create(plan);
        var arguments = FfmpegExportArguments.Create(plan, TestPath("join.partial"), manifestPath);

        Assert.StartsWith("ffconcat version 1.0\n", manifest, StringComparison.Ordinal);
        Assert.Equal(2, manifest.Split("file '", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, manifest.Split("duration 10", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("inpoint", manifest, StringComparison.Ordinal);
        Assert.Equal("concat", ValueAfter(arguments, "-f"));
        Assert.Equal("0:v:0", ValueAfter(arguments, "-map"));
        Assert.Equal("copy", ValueAfter(arguments, "-c"));
        Assert.Contains("0:a:0", arguments);
        Assert.Contains(manifestPath, arguments);
        Assert.DoesNotContain("-filter_complex", arguments);
    }

    [Fact]
    public void Plan_rejects_segments_with_different_codec_extradata()
    {
        var exception = Assert.Throws<ExportPlanException>(() =>
            CreatePlan(CreateSignature("SHA256:first"), CreateSignature("SHA256:second")));

        Assert.Contains("identical encoded stream signatures", exception.Message);
    }

    [Fact]
    public void Manifest_quotes_apostrophes_and_never_emits_trim_directives()
    {
        var plan = CreatePlan(
            CreateSignature("SHA256:video"),
            CreateSignature("SHA256:video"),
            "first clip's source.mp4");

        var manifest = FfconcatManifest.Create(plan);

        Assert.Contains("first clip'\\''s source.mp4'", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("inpoint", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("outpoint", manifest, StringComparison.Ordinal);
    }

    private static ExportPlan CreatePlan(
        VideoStreamCopySignature firstVideo,
        VideoStreamCopySignature secondVideo,
        string firstFileName = "first.mp4")
    {
        var duration = new MediaTime(10, 1);
        var canvas = new PixelSize(1_920, 1_080);
        var audio = new AudioStreamCopySignature(
            "aac",
            "mp4a",
            "SHA256:audio",
            new MediaTime(1, 48_000),
            48_000,
            2,
            "stereo",
            "fltp",
            "LC");
        ExportVideoSegmentPlan Segment(string path, MediaTime start, VideoStreamCopySignature video) => new(
            path,
            0,
            new MediaRange(MediaTime.Zero, duration),
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            [new ExportAudioTrackPlan(1, 0, new SourceEdit(duration))],
            start,
            isCompleteSource: true,
            sourceSize: canvas,
            streamCopyInfo: new SegmentStreamCopyInfo(video, audio, true, true));

        return new ExportPlan(
            [
                Segment(TestPath(firstFileName), MediaTime.Zero, firstVideo),
                Segment(TestPath("second.mp4"), duration, secondVideo),
            ],
            canvas,
            TestPath("joined.mp4"),
            new ExportPreset(
                "mp4-test",
                "MP4",
                ".mp4",
                ExportContainer.Mp4,
                VideoCodecFamily.H264,
                AudioCodecFamily.Aac,
                requiresEvenDimensions: true,
                frameRate: null),
            sequenceDuration: duration * 2,
            strategy: ExportStrategy.ConcatStreamCopy);
    }

    private static VideoStreamCopySignature CreateSignature(string extradataHash) => new(
        "h264",
        "avc1",
        extradataHash,
        new PixelSize(1_920, 1_080),
        new MediaTime(1, 24_000),
        new FrameRate(24_000, 1_001),
        "yuv420p",
        "High",
        40,
        "1:1",
        "tv",
        "bt709",
        "bt709",
        "bt709",
        "progressive");

    private static string TestPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
