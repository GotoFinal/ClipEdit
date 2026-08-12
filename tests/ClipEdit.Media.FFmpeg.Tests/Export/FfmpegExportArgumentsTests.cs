using System.Collections.Immutable;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfmpegExportArgumentsTests
{
    [Fact]
    public void Multiple_kept_ranges_lower_to_split_trim_crop_and_av_concat()
    {
        var plan = CreatePlan(
            "C:\\source media\\weird & name.mkv",
            "C:\\exports\\clip.mp4",
            audioStreamIndex: 2,
            [
                new MediaRange(MediaTime.Zero, new MediaTime(3, 2)),
                new MediaRange(new MediaTime(7, 2), new MediaTime(5, 1)),
            ]);

        var arguments = FfmpegExportArguments.Create(plan, "C:\\exports\\.clip.partial");
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains("[0:0]split=2[vsrc0][vsrc1]", graph);
        Assert.Contains("[0:2]apad,asplit=2[asrc0_0][asrc0_1]", graph);
        Assert.Contains("trim=start=0:end=1.5,setpts=PTS-STARTPTS,crop=1080:1080:420:0,setsar=1[vseg0]", graph);
        Assert.Contains("[vseg0][vseg1]concat=n=2:v=1:a=0[vout]", graph);
        Assert.Contains("[aseg0_0][aseg0_1]concat=n=2:v=0:a=1[atrack0]", graph);
        Assert.Contains("[atrack0]volume=0dB[aout]", graph);
        Assert.Contains("libx264", arguments);
        Assert.Contains("+faststart", arguments);
    }

    [Fact]
    public void External_audio_paths_become_deduplicated_inputs_and_not_filter_text()
    {
        const string source = "C:\\source.mkv";
        const string music = "C:\\audio library\\music & ambience.mka";
        var basePlan = CreatePlan(
            source,
            "C:\\clip.mp4",
            audioStreamIndex: null,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))]);
        var plan = new ExportPlan(
            basePlan.SourcePath,
            basePlan.DestinationPath,
            basePlan.VideoStreamIndex,
            audioStreamIndex: null,
            basePlan.Crop,
            basePlan.SourceRanges,
            basePlan.Preset,
            audioTracks:
            [
                new ExportAudioTrackPlan(1, -3),
                new ExportAudioTrackPlan(music, 0, -9, new MediaTime(3, 2)),
                new ExportAudioTrackPlan(music, 1, -12),
            ]);

        var arguments = FfmpegExportArguments.Create(plan, "C:\\clip.partial");
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal(1, arguments.Count(argument => argument == music));
        Assert.Contains("[0:1]apad,atrim=start=0:end=2", graph);
        Assert.Contains("[1:0]adelay=delays=1.5s:all=1,apad,atrim=start=0:end=2", graph);
        Assert.Contains("[1:1]apad,atrim=start=0:end=2", graph);
        Assert.DoesNotContain(music, graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_embedded_tracks_are_conformed_gained_mixed_and_limited()
    {
        var basePlan = CreatePlan(
            "C:\\source.mkv",
            "C:\\clip.mp4",
            audioStreamIndex: null,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))]);
        var plan = new ExportPlan(
            basePlan.SourcePath,
            basePlan.DestinationPath,
            basePlan.VideoStreamIndex,
            audioStreamIndex: null,
            basePlan.Crop,
            basePlan.SourceRanges,
            basePlan.Preset,
            audioTracks:
            [
                new ExportAudioTrackPlan(1, -6),
                new ExportAudioTrackPlan(2, 3.5),
            ]);

        var graph = FfmpegExportArguments.CreateFilterGraph(plan);

        Assert.Contains("[aseg0_0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=-6dB[amixin0]", graph);
        Assert.Contains("[aseg1_0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=3.5dB[amixin1]", graph);
        Assert.Contains("[amixin0][amixin1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95[aout]", graph);
    }

    [Fact]
    public void Paths_are_individual_arguments_and_never_embedded_in_filter_text()
    {
        const string source = "C:\\clips\\quote' ; $(unsafe).mkv";
        const string output = "C:\\exports\\unicode łódź.partial";
        var plan = CreatePlan(
            source,
            "C:\\exports\\unicode łódź.webm",
            audioStreamIndex: null,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))],
            WebM);

        var arguments = FfmpegExportArguments.Create(plan, output);
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains(source, arguments);
        Assert.Equal(output, arguments[^1]);
        Assert.DoesNotContain(source, graph, StringComparison.Ordinal);
        Assert.Contains("-an", arguments);
        Assert.Contains("libvpx-vp9", arguments);
    }

    private static ExportPlan CreatePlan(
        string sourcePath,
        string destinationPath,
        int? audioStreamIndex,
        ImmutableArray<MediaRange> ranges,
        ExportPreset? preset = null)
    {
        return new ExportPlan(
            sourcePath,
            destinationPath,
            0,
            audioStreamIndex,
            new CropRegion(new PixelSize(1_920, 1_080), 420, 0, 1_080, 1_080),
            ranges,
            preset ?? Mp4Compatible);
    }

    private static ExportPreset Mp4Compatible { get; } = new(
        "mp4",
        "MP4",
        ".mp4",
        ExportContainer.Mp4,
        VideoCodecFamily.H264,
        AudioCodecFamily.Aac,
        requiresEvenDimensions: true);

    private static ExportPreset WebM { get; } = new(
        "webm",
        "WebM",
        ".webm",
        ExportContainer.WebM,
        VideoCodecFamily.Vp9,
        AudioCodecFamily.Opus,
        requiresEvenDimensions: true);
}
