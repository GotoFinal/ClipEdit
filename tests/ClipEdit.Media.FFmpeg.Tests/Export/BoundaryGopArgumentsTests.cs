using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class BoundaryGopArgumentsTests
{
    [Fact]
    public void Boundary_gop_plan_copies_only_the_complete_decode_interval_and_rebuilds_audio()
    {
        var plan = CreatePlan();
        var interiorPath = TestPath("interior.mp4");
        var manifestPath = TestPath("pieces.ffconcat");
        var outputPath = TestPath("candidate.mp4");

        var interior = FfmpegBoundaryGopArguments.CreateInteriorCopy(plan, interiorPath);
        var final = FfmpegBoundaryGopArguments.CreateFinalMux(plan, manifestPath, outputPath);
        var exactFallback = FfmpegExportArguments.CreateExactTranscode(plan, TestPath("fallback.mp4"));

        Assert.Equal("10", ValueAfter(interior, "-ss"));
        Assert.Equal("9.9", ValueAfter(interior, "-t"));
        Assert.Equal("copy", ValueAfter(interior, "-c:v"));
        Assert.Equal("concat", ValueAfter(final, "-f"));
        Assert.Contains(manifestPath, final);
        Assert.Contains(TestPath("source.mp4"), final);
        Assert.Equal("copy", ValueAfter(final, "-c:v"));
        Assert.Contains("aac", final);
        var graph = ValueAfter(final, "-filter_complex");
        Assert.Contains("[1:1]", graph, StringComparison.Ordinal);
        Assert.Contains("atrim=start=6:end=29", graph, StringComparison.Ordinal);
        Assert.Equal("libx264", ValueAfter(exactFallback, "-c:v"));
        Assert.Contains("-filter_complex", exactFallback);
    }

    [Fact]
    public void Boundary_manifest_uses_only_explicit_safe_paths()
    {
        var manifest = FfconcatManifest.CreatePaths(
            [TestPath("lead's.mp4"), TestPath("middle.mp4")]);

        Assert.StartsWith("ffconcat version 1.0\n", manifest, StringComparison.Ordinal);
        Assert.Contains("lead'\\''s.mp4", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("inpoint", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("outpoint", manifest, StringComparison.Ordinal);
    }

    private static ExportPlan CreatePlan()
    {
        var canvas = new PixelSize(1_920, 1_080);
        var range = new MediaRange(new MediaTime(6, 1), new MediaTime(29, 1));
        var signature = new VideoStreamCopySignature(
            "h264",
            "avc1",
            "SHA256:video",
            canvas,
            new MediaTime(1, 30_000),
            new FrameRate(30, 1),
            "yuv420p",
            "High",
            40,
            "1:1",
            "tv",
            "bt709",
            "bt709",
            "bt709",
            "progressive");
        var boundary = new BoundaryGopRenderInfo(
            signature,
            range,
            new MediaTime(10, 1),
            new MediaTime(99, 10),
            new MediaTime(20, 1),
            new MediaTime(199, 10));
        var segment = new ExportVideoSegmentPlan(
            TestPath("source.mp4"),
            0,
            range,
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            [new ExportAudioTrackPlan(1, 0, new SourceEdit(new MediaTime(60, 1)))],
            range.Start,
            sourceSize: canvas,
            boundaryGopInfo: boundary);
        return new ExportPlan(
            [segment],
            canvas,
            TestPath("output.mp4"),
            new ExportPreset(
                "mp4-boundary-test",
                "MP4",
                ".mp4",
                ExportContainer.Mp4,
                VideoCodecFamily.H264,
                AudioCodecFamily.Aac,
                requiresEvenDimensions: true,
                frameRate: new FrameRate(30, 1)),
            sequenceTimelineStart: range.Start,
            sequenceDuration: range.Duration,
            strategy: ExportStrategy.BoundaryGop);
    }

    private static string TestPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
