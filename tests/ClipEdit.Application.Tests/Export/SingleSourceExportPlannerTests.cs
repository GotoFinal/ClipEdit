using System.Collections.Immutable;
using ClipEdit.Application.Export;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.Probe;

namespace ClipEdit.Application.Tests.Export;

public sealed class SingleSourceExportPlannerTests
{
    [Fact]
    public void Planner_preserves_kept_ranges_crop_streams_and_match_crop_output()
    {
        var sourcePath = Path.GetFullPath("source.mkv");
        var media = CreateMedia(sourcePath);
        var edit = new SourceEdit(new MediaTime(10, 1))
            .Remove(new MediaRange(new MediaTime(2, 1), new MediaTime(7, 1)));
        var crop = new CropRegion(new PixelSize(1_920, 1_080), 420, 0, 1_080, 1_080);

        var plan = new SingleSourceExportPlanner().Create(
            media,
            edit,
            crop,
            BuiltInExportPresets.Mp4Compatible,
            Path.GetFullPath("clip.mp4"));

        Assert.Equal(0, plan.VideoStreamIndex);
        Assert.Equal(1, plan.AudioStreamIndex);
        Assert.Equal(crop.ExportSize, plan.OutputSize);
        Assert.Equal(edit.KeptRanges, plan.SourceRanges);
        Assert.Equal(new MediaTime(5, 1), plan.ExpectedDuration);
        Assert.Equal(ExportStrategy.ExactTranscode, plan.Strategy);
    }

    [Fact]
    public void Planner_explains_incompatible_odd_crop_instead_of_silently_rounding_it()
    {
        var media = CreateMedia(Path.GetFullPath("source.mkv"));
        var edit = new SourceEdit(new MediaTime(10, 1));
        var crop = new CropRegion(new PixelSize(1_920, 1_080), 0, 0, 1_919, 1_079);

        var exception = Assert.Throws<ExportPlanException>(() =>
            new SingleSourceExportPlanner().Create(
                media,
                edit,
                crop,
                BuiltInExportPresets.Mp4Compatible,
                Path.GetFullPath("clip.mp4")));

        Assert.Contains("even dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new PixelSize(1_919, 1_079), crop.ExportSize);
    }

    private static ImportedMedia CreateMedia(string sourcePath)
    {
        var streams = ImmutableArray.Create<MediaStreamInfo>(
            new VideoStreamInfo(
                0,
                "h264",
                null,
                null,
                null,
                null,
                true,
                false,
                new MediaTime(1, 1_000),
                MediaTime.Zero,
                new MediaTime(10, 1),
                new PixelSize(1_920, 1_080),
                0,
                new FrameRate(24_000, 1_001),
                new FrameRate(24_000, 1_001),
                "yuv420p",
                "1:1",
                "16:9",
                "tv",
                "bt709",
                "bt709",
                "bt709",
                "progressive"),
            new AudioStreamInfo(
                1,
                "aac",
                null,
                null,
                null,
                null,
                true,
                false,
                new MediaTime(1, 48_000),
                MediaTime.Zero,
                new MediaTime(10, 1),
                48_000,
                2,
                "stereo",
                "fltp"));
        var probe = new MediaProbeResult(
            sourcePath,
            "matroska",
            "Matroska",
            MediaTime.Zero,
            new MediaTime(10, 1),
            1_024,
            8_000,
            streams);
        return new ImportedMedia(Path.GetFileName(sourcePath), probe);
    }
}
