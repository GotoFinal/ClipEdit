using System.Collections.Immutable;
using ClipEdit.Application.Export;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.Probe;

namespace ClipEdit.Application.Tests.Export;

public sealed class MatchInputExportPresetResolverTests
{
    [Fact]
    public void Mp4_h264_aac_preserves_supported_source_parameters()
    {
        var resolution = MatchInputExportPresetResolver.Resolve(CreateProbe(
            "source.mp4",
            "h264",
            "aac",
            videoBitRate: 7_500_000,
            audioBitRate: 192_000));

        Assert.False(resolution.UsedFallback);
        Assert.Equal(ExportContainer.Mp4, resolution.Preset.Container);
        Assert.Equal(VideoCodecFamily.H264, resolution.Preset.VideoCodec);
        Assert.Equal(AudioCodecFamily.Aac, resolution.Preset.AudioCodec);
        Assert.Equal(new FrameRate(24_000, 1_001), resolution.Preset.FrameRate);
        Assert.Equal(7_500_000, resolution.Preset.VideoBitRateBitsPerSecond);
        Assert.Equal(192_000, resolution.Preset.AudioBitRateBitsPerSecond);
        Assert.Equal(".mp4", resolution.Preset.FileExtension);
    }

    [Fact]
    public void Webm_vp9_opus_preserves_supported_source_parameters()
    {
        var resolution = MatchInputExportPresetResolver.Resolve(CreateProbe(
            "source.webm",
            "vp9",
            "opus",
            videoBitRate: 3_000_000,
            audioBitRate: 160_000));

        Assert.False(resolution.UsedFallback);
        Assert.Equal(ExportContainer.WebM, resolution.Preset.Container);
        Assert.Equal(VideoCodecFamily.Vp9, resolution.Preset.VideoCodec);
        Assert.Equal(AudioCodecFamily.Opus, resolution.Preset.AudioCodec);
        Assert.Equal(".webm", resolution.Preset.FileExtension);
    }

    [Fact]
    public void Matroska_keeps_the_container_and_explains_an_unsupported_audio_fallback()
    {
        var resolution = MatchInputExportPresetResolver.Resolve(CreateProbe(
            "source.mkv",
            "h264",
            "flac",
            videoBitRate: 8_000_000,
            audioBitRate: 900_000));

        Assert.True(resolution.UsedFallback);
        Assert.Equal(ExportContainer.Matroska, resolution.Preset.Container);
        Assert.Equal(VideoCodecFamily.H264, resolution.Preset.VideoCodec);
        Assert.Equal(AudioCodecFamily.Aac, resolution.Preset.AudioCodec);
        Assert.Null(resolution.Preset.AudioBitRateBitsPerSecond);
        Assert.Contains("audio codec", resolution.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(".mkv", resolution.Preset.FileExtension);
    }

    private static MediaProbeResult CreateProbe(
        string fileName,
        string videoCodec,
        string audioCodec,
        long videoBitRate,
        long audioBitRate)
    {
        var sourcePath = Path.GetFullPath(fileName);
        return new MediaProbeResult(
            sourcePath,
            Path.GetExtension(fileName).TrimStart('.'),
            null,
            MediaTime.Zero,
            new MediaTime(30, 1),
            1_024,
            videoBitRate + audioBitRate,
            ImmutableArray.Create<MediaStreamInfo>(
                new VideoStreamInfo(
                    0,
                    videoCodec,
                    null,
                    "High",
                    null,
                    null,
                    true,
                    false,
                    new MediaTime(1, 1_000),
                    MediaTime.Zero,
                    new MediaTime(30, 1),
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
                    "progressive",
                    videoBitRate),
                new AudioStreamInfo(
                    1,
                    audioCodec,
                    null,
                    null,
                    null,
                    null,
                    true,
                    false,
                    new MediaTime(1, 48_000),
                    MediaTime.Zero,
                    new MediaTime(30, 1),
                    48_000,
                    2,
                    "stereo",
                    "fltp",
                    audioBitRate)));
    }
}
