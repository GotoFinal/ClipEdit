using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.FFmpeg.Probe;
using ClipEdit.Media.Probe;

namespace ClipEdit.Media.FFmpeg.Tests.Probe;

public sealed class FfprobeJsonParserTests
{
    private const string RepresentativeJson = """
        {
          "streams": [
            {
              "index": 0,
              "codec_name": "h264",
              "codec_long_name": "H.264 / AVC",
              "codec_tag_string": "avc1",
              "extradata_hash": "SHA256:video",
              "profile": "High",
              "level": 40,
              "codec_type": "video",
              "width": 1920,
              "height": 1080,
              "sample_aspect_ratio": "1:1",
              "display_aspect_ratio": "16:9",
              "pix_fmt": "yuv420p",
              "color_range": "tv",
              "color_space": "bt709",
              "color_transfer": "bt709",
              "color_primaries": "bt709",
              "field_order": "progressive",
              "r_frame_rate": "24000/1001",
              "avg_frame_rate": "24000/1001",
              "bit_rate": "7900000",
              "time_base": "1/1000",
              "start_pts": 0,
              "duration_ts": 1420063,
              "disposition": { "default": 1, "forced": 0 },
              "tags": {},
              "side_data_list": [{ "rotation": -90 }]
            },
            {
              "index": 1,
              "codec_name": "aac",
              "codec_type": "audio",
              "codec_tag_string": "mp4a",
              "extradata_hash": "SHA256:audio",
              "profile": "LC",
              "sample_fmt": "fltp",
              "sample_rate": "44100",
              "bit_rate": "192000",
              "channels": 2,
              "channel_layout": "stereo",
              "time_base": "1/1000",
              "start_time": "0.000000",
              "duration": "1420.063000",
              "disposition": { "default": 1, "forced": 0 },
              "tags": { "language": "jpn", "title": "Main audio" }
            },
            {
              "index": 2,
              "codec_name": "ass",
              "codec_type": "subtitle",
              "time_base": "1/1000",
              "duration_ts": 1420063,
              "disposition": { "default": 1, "forced": 0 },
              "tags": { "language": "eng", "title": "English subs" }
            },
            {
              "index": 3,
              "codec_name": "ttf",
              "codec_type": "attachment",
              "time_base": "1/90000",
              "disposition": { "default": 0, "forced": 0 },
              "tags": {}
            }
          ],
          "format": {
            "format_name": "matroska,webm",
            "format_long_name": "Matroska / WebM",
            "start_time": "0.000000",
            "duration": "1420.063000",
            "size": "1448587333",
            "bit_rate": "8160693"
          }
        }
        """;

    [Fact]
    public void Parse_maps_format_and_exact_duration()
    {
        var result = FfprobeJsonParser.Parse("C:\\media\\example.mkv", RepresentativeJson);

        Assert.Equal("matroska,webm", result.FormatName);
        Assert.Equal(new MediaTime(1_420_063, 1_000), result.Duration);
        Assert.Equal(1_448_587_333, result.FileSizeBytes);
        Assert.Equal(8_160_693, result.BitRateBitsPerSecond);
        Assert.Equal(4, result.Streams.Length);
        var video = Assert.Single(result.VideoStreams);
        Assert.Equal("avc1", video.CodecTag);
        Assert.Equal(40, video.CodecLevel);
        Assert.Equal("SHA256:video", video.CodecExtradataHash);
        var audio = Assert.Single(result.AudioStreams);
        Assert.Equal("mp4a", audio.CodecTag);
        Assert.Equal("SHA256:audio", audio.CodecExtradataHash);
    }

    [Fact]
    public void Parse_maps_rotation_corrected_video_metadata()
    {
        var result = FfprobeJsonParser.Parse("C:\\media\\example.mkv", RepresentativeJson);

        var video = Assert.IsType<VideoStreamInfo>(result.Streams[0]);
        Assert.Equal(new PixelSize(1_920, 1_080), video.EncodedSize);
        Assert.Equal(new PixelSize(1_080, 1_920), video.OrientedSize);
        Assert.Equal(270, video.RotationDegrees);
        Assert.Equal(new FrameRate(24_000, 1_001), video.AverageFrameRate);
        Assert.Equal(new MediaTime(1_420_063, 1_000), video.Duration);
        Assert.Equal("bt709", video.ColorSpace);
        Assert.Equal(7_900_000, video.BitRateBitsPerSecond);
    }

    [Fact]
    public void Parse_maps_audio_language_layout_and_duration()
    {
        var result = FfprobeJsonParser.Parse("C:\\media\\example.mkv", RepresentativeJson);

        var audio = Assert.IsType<AudioStreamInfo>(result.Streams[1]);
        Assert.Equal("jpn", audio.Language);
        Assert.Equal("Main audio", audio.Title);
        Assert.Equal(192_000, audio.BitRateBitsPerSecond);
        Assert.Equal(44_100, audio.SampleRate);
        Assert.Equal(2, audio.ChannelCount);
        Assert.Equal("stereo", audio.ChannelLayout);
        Assert.Equal(new MediaTime(1_420_063, 1_000), audio.Duration);
    }

    [Fact]
    public void Parse_uses_mp4_handler_name_when_audio_title_is_absent()
    {
        var json = RepresentativeJson.Replace(
            "\"title\": \"Main audio\"",
            "\"handler_name\": \"Director commentary\"",
            StringComparison.Ordinal);

        var result = FfprobeJsonParser.Parse("C:\\media\\commentary.mp4", json);

        Assert.Equal("Director commentary", Assert.IsType<AudioStreamInfo>(result.Streams[1]).Title);
    }

    [Fact]
    public void Parse_preserves_non_playback_stream_kinds()
    {
        var result = FfprobeJsonParser.Parse("C:\\media\\example.mkv", RepresentativeJson);

        Assert.Equal(MediaStreamKind.Subtitle, result.Streams[2].Kind);
        Assert.Equal(MediaStreamKind.Attachment, result.Streams[3].Kind);
        Assert.Single(result.VideoStreams);
        Assert.Single(result.AudioStreams);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{ not-json }")]
    public void Parse_rejects_invalid_or_incomplete_output(string json)
    {
        var exception = Assert.Throws<MediaProbeException>(
            () => FfprobeJsonParser.Parse("C:\\media\\example.mkv", json));

        Assert.Equal(MediaProbeFailure.InvalidOutput, exception.Failure);
    }
}
