using System.Collections.Immutable;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfmpegExportArgumentsTests
{
    [Fact]
    public void Complete_unchanged_clip_uses_packet_copy_without_a_filter_graph()
    {
        var duration = new MediaTime(10, 1);
        var canvas = new PixelSize(1_920, 1_080);
        var segment = new ExportVideoSegmentPlan(
            TestPath("C:\\source.mp4"),
            0,
            new MediaRange(MediaTime.Zero, duration),
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            [new ExportAudioTrackPlan(1, 0, new SourceEdit(duration))],
            MediaTime.Zero,
            videoColorInfo: null,
            isCompleteSource: true);
        var plan = new ExportPlan(
            [segment],
            canvas,
            TestPath("C:\\copied.mp4"),
            Mp4Compatible,
            sequenceDuration: duration,
            strategy: ExportStrategy.StreamCopy);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.copied.partial"));

        Assert.Equal("copy", ValueAfter(arguments, "-c"));
        Assert.Equal("0:0", ValueAfter(arguments, "-map"));
        Assert.Contains("0:1", arguments);
        Assert.DoesNotContain("-filter_complex", arguments);
        Assert.DoesNotContain("-c:v", arguments);
        Assert.Equal("mp4", ValueAfter(arguments, "-f"));
    }

    [Fact]
    public void Packet_copy_rejects_a_transformed_clip()
    {
        var duration = new MediaTime(10, 1);
        var canvas = new PixelSize(1_920, 1_080);
        var segment = new ExportVideoSegmentPlan(
            TestPath("C:\\source.mp4"),
            0,
            new MediaRange(MediaTime.Zero, duration),
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity.MoveTo(1, 0),
            timelineStart: MediaTime.Zero,
            isCompleteSource: true);

        Assert.Throws<ExportPlanException>(() => new ExportPlan(
            [segment],
            canvas,
            TestPath("C:\\copied.mp4"),
            Mp4Compatible,
            sequenceDuration: duration,
            strategy: ExportStrategy.StreamCopy));
        Assert.Throws<ExportPlanException>(() => new ExportPlan(
            [segment],
            canvas,
            TestPath("C:\\video-copied.mp4"),
            Mp4Compatible,
            sequenceDuration: duration,
            strategy: ExportStrategy.VideoStreamCopy));
    }

    [Fact]
    public void Mp4_edit_list_trim_seeks_without_accurate_decode_and_copies_selected_streams()
    {
        var sourceDuration = new MediaTime(60, 1);
        var range = new MediaRange(new MediaTime(37996, 1000), new MediaTime(88504, 1000));
        var canvas = new PixelSize(1_920, 1_080);
        var segment = new ExportVideoSegmentPlan(
            TestPath("C:\\source.mp4"),
            0,
            range,
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            [new ExportAudioTrackPlan(1, 0, new SourceEdit(sourceDuration))],
            MediaTime.Zero);
        var plan = new ExportPlan(
            [segment],
            canvas,
            TestPath("C:\\trimmed.mp4"),
            Mp4Compatible,
            sequenceDuration: range.Duration,
            strategy: ExportStrategy.EditListStreamCopy);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.trimmed.partial"));

        Assert.Equal("37.996", ValueAfter(arguments, "-ss"));
        var argumentList = arguments.ToList();
        Assert.True(argumentList.IndexOf("-noaccurate_seek") < argumentList.IndexOf("-i"));
        Assert.Equal("50.508", ValueAfter(arguments, "-t"));
        Assert.Contains("0:0", arguments);
        Assert.Contains("0:1", arguments);
        Assert.Equal("copy", ValueAfter(arguments, "-c"));
        Assert.DoesNotContain("-filter_complex", arguments);
        Assert.Equal("mp4", ValueAfter(arguments, "-f"));
    }

    [Fact]
    public void Audio_only_edit_copies_video_and_filters_only_audio()
    {
        var duration = new MediaTime(10, 1);
        var canvas = new PixelSize(1_920, 1_080);
        var segment = new ExportVideoSegmentPlan(
            TestPath("C:\\source.mp4"),
            0,
            new MediaRange(MediaTime.Zero, duration),
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            [new ExportAudioTrackPlan(1, -3, new SourceEdit(duration))],
            MediaTime.Zero,
            videoColorInfo: null,
            isCompleteSource: true);
        var plan = new ExportPlan(
            [segment],
            canvas,
            TestPath("C:\\audio-adjusted.mp4"),
            Mp4Compatible,
            sequenceDuration: duration,
            strategy: ExportStrategy.VideoStreamCopy);

        var arguments = FfmpegExportArguments.Create(
            plan,
            TestPath("C:\\.audio-adjusted.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal("copy", ValueAfter(arguments, "-c:v"));
        Assert.Equal("aac", ValueAfter(arguments, "-c:a"));
        Assert.Equal("0:0", ValueAfter(arguments, "-map"));
        Assert.Contains("[aout]", arguments);
        Assert.Contains("volume=-3dB", graph);
        Assert.DoesNotContain("[vout]", arguments);
        Assert.DoesNotContain("libx264", arguments);
    }

    [Fact]
    public void Keyframe_trim_seeks_video_by_pts_stops_by_dts_and_reads_audio_separately()
    {
        var sourceDuration = new MediaTime(60, 1);
        var range = new MediaRange(new MediaTime(5, 1), new MediaTime(30, 1));
        var canvas = new PixelSize(1_920, 1_080);
        var signature = new VideoStreamCopySignature(
            "h264",
            "avc1",
            "SHA256:video",
            canvas,
            new MediaTime(1, 1_000),
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
        var segment = new ExportVideoSegmentPlan(
            TestPath("C:\\source.mp4"),
            0,
            range,
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            [new ExportAudioTrackPlan(1, 0, new SourceEdit(sourceDuration))],
            range.Start,
            isCompleteSource: false,
            streamCopyInfo: new SegmentStreamCopyInfo(
                signature,
                null,
                true,
                true,
                new MediaTime(49, 10),
                new MediaTime(299, 10)));
        var plan = new ExportPlan(
            [segment],
            canvas,
            TestPath("C:\\keyframe-trim.mp4"),
            Mp4Compatible,
            sequenceTimelineStart: range.Start,
            sequenceDuration: range.Duration,
            strategy: ExportStrategy.VideoStreamCopy);

        var arguments = FfmpegExportArguments.Create(
            plan,
            TestPath("C:\\.keyframe-trim.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal("5", ValueAfter(arguments, "-ss"));
        Assert.Equal("24.9", ValueAfter(arguments, "-t"));
        Assert.Equal(2, arguments.Count(argument => argument == segment.SourcePath));
        Assert.Equal("0:0", ValueAfter(arguments, "-map"));
        Assert.Contains("[1:1]", graph, StringComparison.Ordinal);
        Assert.Contains("atrim=start=5:end=30", graph, StringComparison.Ordinal);
        Assert.Equal("copy", ValueAfter(arguments, "-c:v"));
    }

    [Fact]
    public void External_audio_can_be_mixed_while_video_is_copied()
    {
        var duration = new MediaTime(10, 1);
        var canvas = new PixelSize(1_920, 1_080);
        var music = TestPath("C:\\music.flac");
        var segment = new ExportVideoSegmentPlan(
            TestPath("C:\\source.mp4"),
            0,
            new MediaRange(MediaTime.Zero, duration),
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            audioTracks: [],
            timelineStart: MediaTime.Zero,
            isCompleteSource: true);
        var plan = new ExportPlan(
            [segment],
            canvas,
            TestPath("C:\\music-mix.mp4"),
            Mp4Compatible,
            externalAudioTracks:
            [
                new ExportAudioTrackPlan(
                    music,
                    0,
                    -9,
                    new MediaTime(1, 1),
                    new SourceEdit(duration)),
            ],
            sequenceDuration: duration,
            strategy: ExportStrategy.VideoStreamCopy);

        var arguments = FfmpegExportArguments.Create(
            plan,
            TestPath("C:\\.music-mix.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal(1, arguments.Count(argument => argument == music));
        Assert.Equal("copy", ValueAfter(arguments, "-c:v"));
        Assert.Contains("[1:0]", graph);
        Assert.Contains("adelay=delays=1s:all=1", graph);
        Assert.Contains("volume=-9dB", graph);
        Assert.DoesNotContain(music, graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_kept_ranges_lower_to_split_trim_crop_and_av_concat()
    {
        var plan = CreatePlan(
            TestPath("C:\\source media\\weird & name.mkv"),
            TestPath("C:\\exports\\clip.mp4"),
            audioStreamIndex: 2,
            [
                new MediaRange(MediaTime.Zero, new MediaTime(3, 2)),
                new MediaRange(new MediaTime(7, 2), new MediaTime(5, 1)),
            ]);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\exports\\.clip.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains("[0:0]split=2[vsrc0][vsrc1]", graph);
        Assert.Contains("[0:2]apad,asplit=2[asrc0_0][asrc0_1]", graph);
        Assert.Contains("trim=start=0:end=1.5,setpts=PTS-STARTPTS,crop=1080:1080:420:0,setsar=1[vseg0]", graph);
        Assert.Contains("[vseg0][vseg1]concat=n=2:v=1:a=0[vbase]", graph);
        Assert.Contains("[vbase]scale=1080:1080:flags=lanczos,format=yuv420p,setsar=1[vout]", graph);
        Assert.Contains("[aseg0_0][aseg0_1]concat=n=2:v=0:a=1[atrack0]", graph);
        Assert.Contains("[atrack0]volume=0dB[aout]", graph);
        Assert.Contains("libx264", arguments);
        Assert.Contains("+faststart", arguments);
    }

    [Fact]
    public void External_audio_paths_become_deduplicated_inputs_and_not_filter_text()
    {
        var source = TestPath("C:\\source.mkv");
        var music = TestPath("C:\\audio library\\music & ambience.mka");
        var basePlan = CreatePlan(
            source,
            TestPath("C:\\clip.mp4"),
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
                new ExportAudioTrackPlan(
                    music,
                    0,
                    -9,
                    new MediaTime(3, 2),
                    new SourceEdit(new MediaTime(2, 1)).Remove(
                        new MediaRange(new MediaTime(1, 2), new MediaTime(1, 1)))),
                new ExportAudioTrackPlan(music, 1, -12),
            ]);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\clip.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal(1, arguments.Count(argument => argument == music));
        Assert.Contains("[0:1]apad,atrim=start=0:end=2", graph);
        Assert.Contains(
            "[1:0]aeval='if(gt(gte(t,0)*lt(t,0.5)+gte(t,1)*lt(t,2),0),val(ch),0)':c=same," +
            "adelay=delays=1.5s:all=1,apad,atrim=start=0:end=2",
            graph);
        Assert.Contains("[1:1]apad,atrim=start=0:end=2", graph);
        Assert.DoesNotContain(music, graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_embedded_tracks_are_conformed_gained_mixed_and_limited()
    {
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.mp4"),
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
        var source = TestPath("C:\\clips\\quote' ; $(unsafe).mkv");
        var output = TestPath("C:\\exports\\unicode łódź.partial");
        var plan = CreatePlan(
            source,
            TestPath("C:\\exports\\unicode łódź.webm"),
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

    [Fact]
    public void Multi_source_sequence_uses_one_input_per_segment_and_concatenates_scaled_crops()
    {
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\first.mkv"),
                    0,
                    new MediaRange(new MediaTime(2, 1), new MediaTime(5, 1)),
                    new CropRegion(new PixelSize(1_920, 1_080), 420, 0, 1_080, 1_080),
                    [new ExportAudioTrackPlan(1, -3)]),
                new ExportVideoSegmentPlan(
                    TestPath("C:\\second.mkv"),
                    2,
                    new MediaRange(MediaTime.Zero, new MediaTime(4, 1)),
                    new CropRegion(new PixelSize(3_840, 2_160), 1_080, 0, 1_680, 2_160),
                    [new ExportAudioTrackPlan(3, 0)]),
            ],
            new PixelSize(1_080, 1_080),
            TestPath("C:\\sequence.mp4"),
            Mp4Compatible);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.sequence.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains(TestPath("C:\\first.mkv"), arguments);
        Assert.Contains(TestPath("C:\\second.mkv"), arguments);
        AssertInputSeek(arguments, TestPath("C:\\first.mkv"), "2", "3");
        AssertInputSeek(arguments, TestPath("C:\\second.mkv"), null, "4");
        Assert.Contains("[0:0]trim=start=0:end=3,setpts=PTS-STARTPTS,crop=1080:1080:420:0,scale=1080:1080", graph);
        Assert.Contains("[1:2]trim=start=0:end=4,setpts=PTS-STARTPTS,crop=1680:2160:1080:0,scale=1080:1080", graph);
        Assert.Contains("[vseg0][aseg0][vseg1][aseg1]concat=n=2:v=1:a=1[vbase][abase]", graph);
        Assert.Contains("[vbase]format=yuv420p,setsar=1[vout]", graph);
        Assert.DoesNotContain("[vbase]scale=1080:1080", graph);
        Assert.Contains("[abase]anull[aout]", graph);
    }

    [Fact]
    public void Exact_right_angle_full_cover_uses_direct_transpose_crop_and_single_scale()
    {
        var sourceSize = new PixelSize(3_840, 2_160);
        var canvasSize = new PixelSize(2_160, 3_840);
        var canvasCrop = new CropRegion(canvasSize, 0, 228, 2_160, 2_880);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\recording.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(6, 1)),
                    canvasSize,
                    canvasCrop,
                    new ClipCanvasTransform(0, 0, 1, 1, 270),
                    sourceSize: sourceSize),
            ],
            canvasCrop.ExportSize,
            TestPath("C:\\recording.mp4"),
            Mp4Compatible,
            encodingSettings: new ExportEncodingSettings(scalePercent: 50));

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Equal(new PixelSize(1_080, 1_440), plan.OutputSize);
        Assert.Contains(
            "setpts=PTS-STARTPTS,transpose=dir=cclock," +
            "crop=2160:2880:0:228,scale=1080:1440:flags=lanczos," +
            "format=yuv420p,setsar=1[vseg0]",
            graph);
        Assert.Contains("[vbase]format=yuv420p,setsar=1[vout]", graph);
        Assert.DoesNotContain("rotate=", graph);
        Assert.DoesNotContain("overlay=", graph);
        Assert.DoesNotContain("drawbox=", graph);
        Assert.DoesNotContain("[vbase]scale=", graph);
    }

    [Theory]
    [InlineData(90, "transpose=dir=clock,")]
    [InlineData(180, "hflip,vflip,")]
    [InlineData(270, "transpose=dir=cclock,")]
    public void Exact_quarter_turns_use_axis_aligned_filters(int rotationDegrees, string expectedFilter)
    {
        var sourceSize = new PixelSize(1_920, 1_080);
        var canvasSize = rotationDegrees is 90 or 270
            ? new PixelSize(1_080, 1_920)
            : sourceSize;
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\quarter-turn.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvasSize,
                    CropRegion.FullFrame(canvasSize),
                    new ClipCanvasTransform(0, 0, 1, 1, rotationDegrees),
                    sourceSize: sourceSize),
            ],
            canvasSize,
            TestPath("C:\\quarter-turn.mp4"),
            Mp4Compatible);

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Contains(expectedFilter, graph);
        Assert.DoesNotContain("rotate=", graph);
        Assert.DoesNotContain("overlay=", graph);
    }

    [Fact]
    public void Hdr_quarter_turn_stays_ten_bit_without_rgba_rotation()
    {
        var sourceSize = new PixelSize(1_920, 1_080);
        var canvasSize = new PixelSize(1_080, 1_920);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\hdr-quarter-turn.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvasSize,
                    CropRegion.FullFrame(canvasSize),
                    new ClipCanvasTransform(0, 0, 1, 1, 90),
                    videoColorInfo: Hdr10,
                    sourceSize: sourceSize),
            ],
            canvasSize,
            TestPath("C:\\hdr-quarter-turn.mp4"),
            Mp4Compatible);

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.True(plan.PreservesHdr);
        Assert.Contains("transpose=dir=clock,format=yuv420p10le,setsar=1[vseg0]", graph);
        Assert.Contains("setparams=range=tv:color_primaries=bt2020", graph);
        Assert.DoesNotContain("format=rgba64le", graph);
        Assert.DoesNotContain("overlay=", graph);
    }

    [Fact]
    public void Axis_aligned_clip_smaller_than_crop_uses_black_padding_without_overlay()
    {
        var sourceSize = new PixelSize(640, 360);
        var canvasSize = new PixelSize(1_280, 720);
        var canvasCrop = CropRegion.FullFrame(canvasSize);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\small.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvasSize,
                    canvasCrop,
                    ClipCanvasTransform.Identity,
                    sourceSize: sourceSize),
            ],
            canvasSize,
            TestPath("C:\\padded.mp4"),
            Mp4Compatible);

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Contains("pad=1280:720:320:180:color=black,format=yuv420p,setsar=1[vseg0]", graph);
        Assert.DoesNotContain("split=2[vseg0basein]", graph);
        Assert.DoesNotContain("overlay=", graph);
    }

    [Fact]
    public void Fractional_right_angle_placement_keeps_overlay_fallback_but_uses_transpose()
    {
        var sourceSize = new PixelSize(640, 360);
        var canvasSize = new PixelSize(1_280, 720);
        var canvasCrop = CropRegion.FullFrame(canvasSize);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\fractional.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvasSize,
                    canvasCrop,
                    new ClipCanvasTransform(0.5, 0, 1, 1, 90),
                    sourceSize: sourceSize),
            ],
            canvasSize,
            TestPath("C:\\fractional.mp4"),
            Mp4Compatible);

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Contains("split=2[vseg0basein][vseg0contentin]", graph);
        Assert.DoesNotContain(",[vseg0content]", graph, StringComparison.Ordinal);
        Assert.Contains(
            "scale=2:2:flags=fast_bilinear,drawbox=c=black:t=fill," +
            "pad=1280:720:0:0:color=black[vseg0base]",
            graph);
        Assert.Contains("[vseg0contentin]transpose=dir=clock,scale=round(iw*1):round(ih*1)", graph);
        Assert.Contains("overlay=x=(W-w)/2+0.5", graph);
        Assert.DoesNotContain("format=rgba,rotate=90", graph);
    }

    [Fact]
    public void Canvas_transform_without_known_source_size_keeps_safe_overlay_fallback()
    {
        var canvasSize = new PixelSize(1_280, 720);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\unknown-size.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvasSize,
                    CropRegion.FullFrame(canvasSize),
                    ClipCanvasTransform.Identity),
            ],
            canvasSize,
            TestPath("C:\\unknown-size.mp4"),
            Mp4Compatible);

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Null(Assert.Single(plan.VideoSegments).SourceSize);
        Assert.Contains("split=2[vseg0basein][vseg0contentin]", graph);
        Assert.Contains("overlay=x=(W-w)/2+0:y=(H-h)/2+0", graph);
    }

    [Fact]
    public void Canvas_sequence_places_mirrored_scaled_rotated_clip_beneath_shared_crop()
    {
        var canvasSize = new PixelSize(1_920, 1_080);
        var canvasCrop = new CropRegion(canvasSize, 420, 0, 1_080, 1_080);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\first.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvasSize,
                    canvasCrop,
                    new ClipCanvasTransform(
                        120.5,
                        -40,
                        1.25,
                        0.75,
                        15,
                        isHorizontallyMirrored: true,
                        isVerticallyMirrored: true)),
            ],
            canvasCrop.ExportSize,
            TestPath("C:\\canvas.mp4"),
            Mp4Compatible);

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Contains(
            "setpts=PTS-STARTPTS,split=2[vseg0basein][vseg0contentin]",
            graph);
        Assert.Contains(
            "scale=2:2:flags=fast_bilinear,drawbox=c=black:t=fill," +
            "pad=1920:1080:0:0:color=black[vseg0base]",
            graph);
        Assert.Contains(
            "hflip,vflip,format=rgba,rotate=15*PI/180:" +
            "ow=rotw(15*PI/180):oh=roth(15*PI/180):c=black@0," +
            "scale=round(iw*1.25):round(ih*0.75):flags=lanczos",
            graph);
        Assert.DoesNotContain("rotw(iw)", graph);
        Assert.DoesNotContain("roth(ih)", graph);
        Assert.Contains(
            "overlay=x=(W-w)/2+120.5:y=(H-h)/2-40:shortest=1:format=yuv420," +
            "crop=1080:1080:420:0",
            graph);
    }

    [Fact]
    public void Compatible_hdr10_source_exports_ten_bit_pixels_and_explicit_color_signaling()
    {
        var hdr = Hdr10;
        var basePlan = CreatePlan(
            TestPath("C:\\hdr.mp4"),
            TestPath("C:\\hdr-export.mp4"),
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
            sourceVideoColorInfo: hdr);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.hdr.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.True(plan.PreservesHdr);
        Assert.Contains("format=yuv420p10le,setparams=range=tv:color_primaries=bt2020:" +
                        "color_trc=smpte2084:colorspace=bt2020nc", graph);
        Assert.Equal("yuv420p10le", ValueAfter(arguments, "-pix_fmt"));
        Assert.Equal("high10", ValueAfter(arguments, "-profile:v"));
        Assert.Equal("tv", ValueAfter(arguments, "-color_range"));
        Assert.Equal("bt2020", ValueAfter(arguments, "-color_primaries"));
        Assert.Equal("smpte2084", ValueAfter(arguments, "-color_trc"));
        Assert.Equal("bt2020nc", ValueAfter(arguments, "-colorspace"));
    }

    [Fact]
    public void Hdr_canvas_transform_keeps_high_precision_rotation_and_overlay()
    {
        var canvas = new PixelSize(1_280, 720);
        var crop = CropRegion.FullFrame(canvas);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\hdr.mp4"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvas,
                    crop,
                    new ClipCanvasTransform(0, 0, 0.8, 0.8, 17),
                    videoColorInfo: Hdr10),
            ],
            canvas,
            TestPath("C:\\hdr-transform.mp4"),
            Mp4Compatible);

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.True(plan.PreservesHdr);
        Assert.Contains("format=rgba64le,rotate=17*PI/180", graph);
        Assert.Contains("shortest=1:format=yuv420p10", graph);
        Assert.Contains("format=yuv420p10le", graph);
        Assert.DoesNotContain("format=rgba,rotate", graph);
    }

    [Fact]
    public void Mixed_hdr_and_sdr_sequence_tone_maps_hdr_to_a_common_sdr_output()
    {
        var canvas = new PixelSize(1_280, 720);
        var crop = CropRegion.FullFrame(canvas);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\hdr.mp4"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(1, 1)),
                    canvas,
                    crop,
                    ClipCanvasTransform.Identity,
                    videoColorInfo: Hdr10),
                new ExportVideoSegmentPlan(
                    TestPath("C:\\sdr.mp4"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(1, 1)),
                    canvas,
                    crop,
                    ClipCanvasTransform.Identity,
                    videoColorInfo: new ExportVideoColorInfo(
                        "yuv420p", "tv", "bt709", "bt709", "bt709")),
            ],
            canvas,
            TestPath("C:\\mixed.mp4"),
            Mp4Compatible);

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);
        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.mixed.partial"));

        Assert.False(plan.PreservesHdr);
        Assert.Contains("zscale=transfer=linear:npl=100", graph);
        Assert.Contains("tonemap=mobius:desat=0", graph);
        Assert.Equal(1, graph.Split("tonemap=mobius", StringSplitOptions.None).Length - 1);
        Assert.Equal("yuv420p", ValueAfter(arguments, "-pix_fmt"));
        Assert.DoesNotContain("-color_trc", arguments);
    }

    [Fact]
    public void Sequence_gaps_lower_to_black_frames_and_silence()
    {
        var canvas = new PixelSize(1_280, 720);
        var crop = CropRegion.FullFrame(canvas);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\source.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvas,
                    crop,
                    ClipCanvasTransform.Identity,
                    [new ExportAudioTrackPlan(1, 0)],
                    new MediaTime(3, 1)),
            ],
            canvas,
            TestPath("C:\\gapped.mp4"),
            Mp4Compatible,
            sequenceDuration: new MediaTime(7, 1));

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Equal(new MediaTime(7, 1), plan.ExpectedDuration);
        Assert.Contains(
            "color=c=black:s=1280x720:r=30:d=3,format=yuv420p,setsar=1[vgap0]",
            graph);
        Assert.Contains(
            "color=c=black:s=1280x720:r=30:d=2,format=yuv420p,setsar=1[vgap1]",
            graph);
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=3,aformat=sample_fmts=fltp:channel_layouts=stereo[agap0]", graph);
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=2,aformat=sample_fmts=fltp:channel_layouts=stereo[agap1]", graph);
        Assert.Contains(
            "[vgap0][agap0][vseg0][aseg0][vgap1][agap1]concat=n=3:v=1:a=1[vbase][abase]",
            graph);
    }

    [Fact]
    public void Clip_and_global_playback_speed_scale_video_duration_and_pitch_preserved_audio()
    {
        var canvas = new PixelSize(1_280, 720);
        var crop = CropRegion.FullFrame(canvas);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\source.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(8, 1)),
                    canvas,
                    crop,
                    ClipCanvasTransform.Identity,
                    [new ExportAudioTrackPlan(1, 0)],
                    MediaTime.Zero,
                    playbackSpeedPercent: 200),
            ],
            canvas,
            TestPath("C:\\sped-up.mp4"),
            Mp4Compatible,
            encodingSettings: new ExportEncodingSettings(playbackSpeedPercent: 400));

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Equal(new MediaTime(4, 1), plan.TimelineDuration);
        Assert.Equal(new MediaTime(1, 1), plan.ExpectedDuration);
        Assert.Contains("trim=start=0:end=8,setpts=(PTS-STARTPTS)/2", graph);
        Assert.Contains("asetpts=PTS-STARTPTS,atempo=2,aresample=48000", graph);
        Assert.Contains("[vbase]setpts=(PTS-STARTPTS)/4,format=yuv420p,setsar=1[vout]", graph);
        Assert.DoesNotContain("[vbase]setpts=(PTS-STARTPTS)/4,scale=1280:720", graph);
        Assert.Contains("[abase]atempo=2,atempo=2[aout]", graph);
    }

    [Theory]
    [InlineData(1, "[abase]atempo=0.5,atempo=0.5,atempo=0.5,atempo=0.5,atempo=0.5,atempo=0.5,atempo=0.64[aout]")]
    [InlineData(10000, "[abase]atempo=2,atempo=2,atempo=2,atempo=2,atempo=2,atempo=2,atempo=1.5625[aout]")]
    public void Extreme_supported_export_speeds_chain_pitch_preserving_audio_stages(
        int playbackSpeedPercent,
        string expectedAudioFilter)
    {
        var canvas = new PixelSize(1_280, 720);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\source.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(8, 1)),
                    canvas,
                    CropRegion.FullFrame(canvas),
                    ClipCanvasTransform.Identity,
                    [new ExportAudioTrackPlan(1, 0)],
                    MediaTime.Zero),
            ],
            canvas,
            TestPath("C:\\speed-limit.mp4"),
            Mp4Compatible,
            encodingSettings: new ExportEncodingSettings(
                playbackSpeedPercent: playbackSpeedPercent));

        var graph = FfmpegExportArguments.CreateSequenceFilterGraph(plan);

        Assert.Contains(expectedAudioFilter, graph);
    }

    [Fact]
    public void Matched_parameters_use_bitrate_mode_rational_frame_rate_and_matroska_muxing()
    {
        var preset = new ExportPreset(
            "resolved-match",
            "Match input — MKV",
            ".mkv",
            ExportContainer.Matroska,
            VideoCodecFamily.H264,
            AudioCodecFamily.Aac,
            requiresEvenDimensions: true,
            frameRate: new FrameRate(24_000, 1_001),
            videoBitRateBitsPerSecond: 7_500_000,
            audioBitRateBitsPerSecond: 192_000);
        var plan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\matched.mkv"),
            audioStreamIndex: 2,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))],
            preset);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.matched.partial"));

        Assert.DoesNotContain("-crf", arguments);
        Assert.Equal("7500000", ValueAfter(arguments, "-b:v"));
        Assert.Equal("24000/1001", ValueAfter(arguments, "-r"));
        Assert.Equal("192000", ValueAfter(arguments, "-b:a"));
        Assert.Equal("matroska", ValueAfter(arguments, "-f"));
    }

    [Fact]
    public void Gif_uses_scaled_palette_pipeline_configured_frame_rate_and_no_audio()
    {
        var gifPreset = new ExportPreset(
            "gif",
            "Animated GIF",
            ".gif",
            ExportContainer.Gif,
            VideoCodecFamily.Gif,
            AudioCodecFamily.None,
            requiresEvenDimensions: false);
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.gif"),
            audioStreamIndex: 2,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))],
            gifPreset);
        var plan = new ExportPlan(
            basePlan.SourcePath,
            basePlan.DestinationPath,
            basePlan.VideoStreamIndex,
            audioStreamIndex: 2,
            basePlan.Crop,
            basePlan.SourceRanges,
            gifPreset,
            encodingSettings: new ExportEncodingSettings(50, 50, 12));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal(new PixelSize(540, 540), plan.OutputSize);
        Assert.Contains("[vbase]fps=12,scale=540:540:flags=lanczos,split=2[gifsource][gifpaletteinput]", graph);
        Assert.Contains("palettegen=max_colors=143:stats_mode=diff[gifpalette]", graph);
        Assert.Contains("paletteuse=dither=sierra2_4a:diff_mode=rectangle[vout]", graph);
        Assert.Contains("-an", arguments);
        Assert.Equal("gif", ValueAfter(arguments, "-c:v"));
        Assert.Equal("0", ValueAfter(arguments, "-loop"));
        Assert.Equal("gif", ValueAfter(arguments, "-f"));
        Assert.DoesNotContain("-c:a", arguments);
    }

    [Theory]
    [InlineData(1, "36")]
    [InlineData(75, "20")]
    [InlineData(100, "16")]
    public void Global_quality_changes_h264_compression(int quality, string expectedCrf)
    {
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.mp4"),
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
            encodingSettings: new ExportEncodingSettings(quality));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));

        Assert.Equal(expectedCrf, ValueAfter(arguments, "-crf"));
    }

    [Fact]
    public void Target_bitrate_mode_sets_an_exact_video_bitrate_without_scaling_audio()
    {
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.mp4"),
            audioStreamIndex: 1,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))]);
        var plan = new ExportPlan(
            basePlan.SourcePath,
            basePlan.DestinationPath,
            basePlan.VideoStreamIndex,
            audioStreamIndex: 1,
            basePlan.Crop,
            basePlan.SourceRanges,
            basePlan.Preset,
            encodingSettings: new ExportEncodingSettings(
                quality: 1,
                qualityMode: ExportQualityMode.BitRate,
                videoBitRateKbps: 6_000));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));

        Assert.Equal("6000000", ValueAfter(arguments, "-b:v"));
        Assert.Equal("192000", ValueAfter(arguments, "-b:a"));
        Assert.DoesNotContain("-crf", arguments);
    }

    [Fact]
    public void Sequence_input_seek_rebases_embedded_audio_masks_to_the_bounded_input()
    {
        var sourceDuration = new MediaTime(20, 1);
        var sourceRange = new MediaRange(new MediaTime(10, 1), new MediaTime(15, 1));
        var audioEdit = new SourceEdit(sourceDuration)
            .Remove(new MediaRange(new MediaTime(11, 1), new MediaTime(12, 1)));
        var canvas = new PixelSize(1_280, 720);
        var segment = new ExportVideoSegmentPlan(
            TestPath("C:\\source.mkv"),
            0,
            sourceRange,
            canvas,
            CropRegion.FullFrame(canvas),
            ClipCanvasTransform.Identity,
            [new ExportAudioTrackPlan(1, 0, audioEdit)],
            MediaTime.Zero,
            sourceSize: canvas);
        var plan = new ExportPlan(
            [segment],
            canvas,
            TestPath("C:\\clip.mp4"),
            Mp4Compatible,
            sequenceDuration: sourceRange.Duration);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        AssertInputSeek(arguments, segment.SourcePath, "10", "5");
        Assert.Contains("[0:0]trim=start=0:end=5", graph);
        Assert.Contains(
            "[0:1]aeval='if(gt(gte(t,0)*lt(t,1)+gte(t,2)*lt(t,5),0),val(ch),0)':c=same," +
            "apad,atrim=start=0:end=5",
            graph);
        Assert.Contains("[vseg0]null[vbase]", graph);
        Assert.Contains("[aseg0]anull[abase]", graph);
        Assert.DoesNotContain("concat=n=1", graph);
    }

    [Fact]
    public void Av1_uses_libaom_constant_quality_and_parallel_encoding_options()
    {
        var av1 = new ExportPreset(
            "av1-webm",
            "AV1 WebM",
            ".webm",
            ExportContainer.WebM,
            VideoCodecFamily.Av1,
            AudioCodecFamily.Opus,
            requiresEvenDimensions: true);
        var plan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.webm"),
            audioStreamIndex: null,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))],
            av1);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));

        Assert.Equal("libaom-av1", ValueAfter(arguments, "-c:v"));
        Assert.Equal("30", ValueAfter(arguments, "-crf"));
        Assert.Equal("0", ValueAfter(arguments, "-b:v"));
        Assert.Equal("6", ValueAfter(arguments, "-cpu-used"));
        Assert.Equal("1", ValueAfter(arguments, "-row-mt"));
        Assert.Equal("webm", ValueAfter(arguments, "-f"));
    }

    [Theory]
    [InlineData(ExportContainer.Mp4, VideoCodecFamily.Hevc, AudioCodecFamily.Aac, ".mp4", "libx265", "aac", "mp4")]
    [InlineData(ExportContainer.WebM, VideoCodecFamily.Vp8, AudioCodecFamily.Vorbis, ".webm", "libvpx", "libvorbis", "webm")]
    [InlineData(ExportContainer.Matroska, VideoCodecFamily.Hevc, AudioCodecFamily.Flac, ".mkv", "libx265", "flac", "matroska")]
    public void Extended_custom_codecs_map_to_ffmpeg_encoders(
        ExportContainer container,
        VideoCodecFamily videoCodec,
        AudioCodecFamily audioCodec,
        string extension,
        string expectedVideoEncoder,
        string expectedAudioEncoder,
        string expectedMuxer)
    {
        var preset = new ExportPreset(
            "extended-custom",
            "Extended custom",
            extension,
            container,
            videoCodec,
            audioCodec,
            requiresEvenDimensions: true);
        var plan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath($"C:\\clip{extension}"),
            audioStreamIndex: 1,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))],
            preset);

        var arguments = FfmpegExportArguments.Create(plan, TestPath($"C:\\.clip.partial{extension}"));

        Assert.Equal(expectedVideoEncoder, ValueAfter(arguments, "-c:v"));
        Assert.Equal(expectedAudioEncoder, ValueAfter(arguments, "-c:a"));
        Assert.Equal(expectedMuxer, ValueAfter(arguments, "-f"));
        if (videoCodec == VideoCodecFamily.Hevc && container == ExportContainer.Mp4)
        {
            Assert.Equal("hvc1", ValueAfter(arguments, "-tag:v"));
        }
        if (audioCodec == AudioCodecFamily.Flac)
        {
            Assert.DoesNotContain("-b:a", arguments);
        }
    }

    [Fact]
    public void Vp8_export_tone_maps_hdr_to_supported_eight_bit_video()
    {
        var sourceSize = new PixelSize(1_920, 1_080);
        var preset = new ExportPreset(
            "vp8-webm",
            "VP8 WebM",
            ".webm",
            ExportContainer.WebM,
            VideoCodecFamily.Vp8,
            AudioCodecFamily.None,
            requiresEvenDimensions: true);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\hdr.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    sourceSize,
                    CropRegion.FullFrame(sourceSize),
                    ClipCanvasTransform.Identity,
                    videoColorInfo: Hdr10,
                    sourceSize: sourceSize),
            ],
            sourceSize,
            TestPath("C:\\vp8.webm"),
            preset);

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.vp8.partial"));
        var graph = ValueAfter(arguments, "-filter_complex");

        Assert.False(plan.PreservesHdr);
        Assert.Contains("tonemap=mobius", graph);
        Assert.Equal("yuv420p", ValueAfter(arguments, "-pix_fmt"));
    }

    [Theory]
    [InlineData(ExportEncodingSpeed.Faster, "veryfast")]
    [InlineData(ExportEncodingSpeed.Balanced, "medium")]
    [InlineData(ExportEncodingSpeed.SmallerFile, "slow")]
    public void H264_encoding_speed_maps_to_an_explicit_x264_preset(
        ExportEncodingSpeed speed,
        string expectedPreset)
    {
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.mp4"),
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
            encodingSettings: new ExportEncodingSettings(encodingSpeed: speed));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));

        Assert.Equal(expectedPreset, ValueAfter(arguments, "-preset"));
    }

    [Theory]
    [InlineData(ExportVideoEncoder.NvidiaNvenc, "h264_nvenc", "-preset", "p4")]
    [InlineData(ExportVideoEncoder.IntelQuickSync, "h264_qsv", "-preset", "medium")]
    [InlineData(ExportVideoEncoder.AmdAmf, "h264_amf", "-quality", "balanced")]
    public void Validated_hardware_encoder_selection_lowers_to_backend_specific_h264_arguments(
        ExportVideoEncoder videoEncoder,
        string expectedCodec,
        string speedOption,
        string expectedSpeed)
    {
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.mp4"),
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
            encodingSettings: new ExportEncodingSettings(
                qualityMode: ExportQualityMode.Custom,
                videoEncoder: videoEncoder));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));

        Assert.Equal(expectedCodec, ValueAfter(arguments, "-c:v"));
        Assert.Equal(expectedSpeed, ValueAfter(arguments, speedOption));
        Assert.Contains(videoEncoder == ExportVideoEncoder.IntelQuickSync ? "-global_quality" : "-rc", arguments);
    }

    [Fact]
    public void Vaapi_encoder_uploads_the_software_filter_result_to_its_validated_device()
    {
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.mp4"),
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
            encodingSettings: new ExportEncodingSettings(
                qualityMode: ExportQualityMode.Custom,
                videoEncoder: ExportVideoEncoder.Vaapi));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));

        Assert.Equal("vaapi=clipeditva", ValueAfter(arguments, "-init_hw_device"));
        Assert.Equal("clipeditva", ValueAfter(arguments, "-filter_hw_device"));
        Assert.Contains("[vout]format=nv12,hwupload[vencoder]", ValueAfter(arguments, "-filter_complex"));
        Assert.Equal("[vencoder]", ValueAfter(arguments, "-map"));
        Assert.Equal("h264_vaapi", ValueAfter(arguments, "-c:v"));
    }

    [Fact]
    public void Unsupported_hardware_encoder_setting_does_not_replace_a_vp9_encoder()
    {
        var vp9 = new ExportPreset(
            "vp9-test",
            "VP9 test",
            ".webm",
            ExportContainer.WebM,
            VideoCodecFamily.Vp9,
            AudioCodecFamily.None,
            requiresEvenDimensions: true);
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath("C:\\clip.webm"),
            audioStreamIndex: null,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))],
            vp9);
        var plan = new ExportPlan(
            basePlan.SourcePath,
            basePlan.DestinationPath,
            basePlan.VideoStreamIndex,
            audioStreamIndex: null,
            basePlan.Crop,
            basePlan.SourceRanges,
            basePlan.Preset,
            encodingSettings: new ExportEncodingSettings(
                videoEncoder: ExportVideoEncoder.NvidiaNvenc));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));

        Assert.Equal("libvpx-vp9", ValueAfter(arguments, "-c:v"));
        Assert.DoesNotContain("h264_nvenc", arguments);
    }

    [Theory]
    [InlineData(VideoCodecFamily.Hevc, ExportVideoEncoder.NvidiaNvenc, ExportContainer.Mp4, "hevc_nvenc")]
    [InlineData(VideoCodecFamily.Hevc, ExportVideoEncoder.IntelQuickSync, ExportContainer.Mp4, "hevc_qsv")]
    [InlineData(VideoCodecFamily.Hevc, ExportVideoEncoder.AmdAmf, ExportContainer.Mp4, "hevc_amf")]
    [InlineData(VideoCodecFamily.Vp9, ExportVideoEncoder.IntelQuickSync, ExportContainer.WebM, "vp9_qsv")]
    [InlineData(VideoCodecFamily.Av1, ExportVideoEncoder.NvidiaNvenc, ExportContainer.WebM, "av1_nvenc")]
    [InlineData(VideoCodecFamily.Av1, ExportVideoEncoder.IntelQuickSync, ExportContainer.WebM, "av1_qsv")]
    [InlineData(VideoCodecFamily.Av1, ExportVideoEncoder.AmdAmf, ExportContainer.WebM, "av1_amf")]
    public void Supported_hardware_codec_pairs_use_backend_specific_encoders(
        VideoCodecFamily videoCodec,
        ExportVideoEncoder videoEncoder,
        ExportContainer container,
        string expectedEncoder)
    {
        var extension = container == ExportContainer.Mp4 ? ".mp4" : ".webm";
        var preset = new ExportPreset(
            "hardware-codec",
            "Hardware codec",
            extension,
            container,
            videoCodec,
            AudioCodecFamily.None,
            requiresEvenDimensions: true);
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath($"C:\\clip{extension}"),
            audioStreamIndex: null,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))],
            preset);
        var plan = new ExportPlan(
            basePlan.SourcePath,
            basePlan.DestinationPath,
            basePlan.VideoStreamIndex,
            audioStreamIndex: null,
            basePlan.Crop,
            basePlan.SourceRanges,
            preset,
            encodingSettings: new ExportEncodingSettings(
                qualityMode: ExportQualityMode.Custom,
                videoEncoder: videoEncoder));

        var arguments = FfmpegExportArguments.Create(plan, TestPath($"C:\\.clip.partial{extension}"));

        Assert.Equal(expectedEncoder, ValueAfter(arguments, "-c:v"));
    }

    [Theory]
    [InlineData(VideoCodecFamily.H264, "h264_vaapi", ExportContainer.Mp4)]
    [InlineData(VideoCodecFamily.Hevc, "hevc_vaapi", ExportContainer.Mp4)]
    [InlineData(VideoCodecFamily.Vp8, "vp8_vaapi", ExportContainer.WebM)]
    [InlineData(VideoCodecFamily.Vp9, "vp9_vaapi", ExportContainer.WebM)]
    [InlineData(VideoCodecFamily.Av1, "av1_vaapi", ExportContainer.WebM)]
    public void Vaapi_supports_each_custom_video_codec(
        VideoCodecFamily videoCodec,
        string expectedEncoder,
        ExportContainer container)
    {
        var extension = container == ExportContainer.Mp4 ? ".mp4" : ".webm";
        var preset = new ExportPreset(
            "vaapi-codec",
            "VA-API codec",
            extension,
            container,
            videoCodec,
            AudioCodecFamily.None,
            requiresEvenDimensions: true);
        var basePlan = CreatePlan(
            TestPath("C:\\source.mkv"),
            TestPath($"C:\\clip{extension}"),
            audioStreamIndex: null,
            [new MediaRange(MediaTime.Zero, new MediaTime(2, 1))],
            preset);
        var plan = new ExportPlan(
            basePlan.SourcePath,
            basePlan.DestinationPath,
            basePlan.VideoStreamIndex,
            audioStreamIndex: null,
            basePlan.Crop,
            basePlan.SourceRanges,
            preset,
            encodingSettings: new ExportEncodingSettings(videoEncoder: ExportVideoEncoder.Vaapi));

        var arguments = FfmpegExportArguments.Create(plan, TestPath($"C:\\.clip.partial{extension}"));

        Assert.Equal(expectedEncoder, ValueAfter(arguments, "-c:v"));
        Assert.Contains("[vout]format=nv12,hwupload[vencoder]", ValueAfter(arguments, "-filter_complex"));
    }

    [Fact]
    public void Hdr_export_keeps_software_encoder_until_ten_bit_hardware_is_probed()
    {
        var canvas = new PixelSize(1_920, 1_080);
        var preset = new ExportPreset(
            "hdr-hevc",
            "HDR HEVC",
            ".mp4",
            ExportContainer.Mp4,
            VideoCodecFamily.Hevc,
            AudioCodecFamily.None,
            requiresEvenDimensions: true);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\hdr.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                    canvas,
                    CropRegion.FullFrame(canvas),
                    ClipCanvasTransform.Identity,
                    videoColorInfo: Hdr10,
                    sourceSize: canvas),
            ],
            canvas,
            TestPath("C:\\hdr.mp4"),
            preset,
            encodingSettings: new ExportEncodingSettings(videoEncoder: ExportVideoEncoder.NvidiaNvenc));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.hdr.partial"));

        Assert.True(plan.PreservesHdr);
        Assert.Equal("libx265", ValueAfter(arguments, "-c:v"));
    }

    [Fact]
    public void Vulkan_decode_is_applied_to_each_bounded_video_input_only()
    {
        var canvas = new PixelSize(1_280, 720);
        var plan = new ExportPlan(
            [
                new ExportVideoSegmentPlan(
                    TestPath("C:\\first.mkv"),
                    0,
                    new MediaRange(new MediaTime(2, 1), new MediaTime(5, 1)),
                    canvas,
                    CropRegion.FullFrame(canvas),
                    ClipCanvasTransform.Identity,
                    sourceSize: canvas),
                new ExportVideoSegmentPlan(
                    TestPath("C:\\second.mkv"),
                    0,
                    new MediaRange(MediaTime.Zero, new MediaTime(4, 1)),
                    canvas,
                    CropRegion.FullFrame(canvas),
                    ClipCanvasTransform.Identity,
                    sourceSize: canvas),
            ],
            canvas,
            TestPath("C:\\clip.mp4"),
            Mp4Compatible,
            encodingSettings: new ExportEncodingSettings(
                hardwareAcceleration: ExportHardwareAcceleration.Vulkan));

        var arguments = FfmpegExportArguments.Create(plan, TestPath("C:\\.clip.partial"));

        Assert.Equal(2, arguments.Count(argument => argument == "-hwaccel"));
        Assert.Equal(2, arguments.Count(argument => argument == "vulkan"));
        AssertInputSeek(arguments, TestPath("C:\\first.mkv"), "2", "3");
        AssertInputSeek(arguments, TestPath("C:\\second.mkv"), null, "4");
    }

    private static string TestPath(string windowsPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return windowsPath;
        }

        var relativePath = windowsPath.Length >= 3 &&
                           char.IsAsciiLetter(windowsPath[0]) &&
                           windowsPath[1] == ':' &&
                           windowsPath[2] == '\\'
            ? windowsPath[3..]
            : windowsPath;
        return Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "ClipEdit.Tests",
            relativePath.Replace('\\', Path.DirectorySeparatorChar)));
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0 && index + 1 < arguments.Count);
        return arguments[index + 1];
    }

    private static void AssertInputSeek(
        IReadOnlyList<string> arguments,
        string sourcePath,
        string? expectedSeek,
        string expectedDuration)
    {
        var inputIndex = arguments.ToList().IndexOf(sourcePath);
        Assert.True(inputIndex >= 3);
        Assert.Equal("-i", arguments[inputIndex - 1]);
        Assert.Equal(expectedDuration, arguments[inputIndex - 2]);
        Assert.Equal("-t", arguments[inputIndex - 3]);
        if (expectedSeek is null)
        {
            Assert.True(inputIndex < 5 || arguments[inputIndex - 5] != "-ss");
            return;
        }

        Assert.True(inputIndex >= 5);
        Assert.Equal(expectedSeek, arguments[inputIndex - 4]);
        Assert.Equal("-ss", arguments[inputIndex - 5]);
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

    private static ExportVideoColorInfo Hdr10 { get; } = new(
        "yuv420p10le",
        "tv",
        "bt2020nc",
        "smpte2084",
        "bt2020");
}
