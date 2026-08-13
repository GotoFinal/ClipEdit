using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.Probe;

namespace ClipEdit.Application.Export;

public sealed record MatchInputExportResolution(
    ExportPreset Preset,
    bool UsedFallback,
    string Explanation);

public static class MatchInputExportPresetResolver
{
    private const long MaximumVideoBitRate = 1_000_000_000;
    private const long MaximumAudioBitRate = 10_000_000;

    public static MatchInputExportResolution Resolve(MediaProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var video = probe.VideoStreams.FirstOrDefault() ??
                    throw new ExportPlanException("Match input requires a source video stream.");
        var audio = probe.AudioStreams.FirstOrDefault();

        var videoMatched = TryMapVideoCodec(video.CodecName, out var videoCodec);
        if (!videoMatched)
        {
            videoCodec = VideoCodecFamily.H264;
        }

        var audioCodec = AudioCodecFamily.Aac;
        var audioMatched = audio is null || TryMapAudioCodec(audio.CodecName, out audioCodec);
        if (audio is null)
        {
            audioCodec = AudioCodecFamily.Aac;
        }
        else if (!audioMatched)
        {
            audioCodec = AudioCodecFamily.Aac;
        }

        var sourceContainer = InferSourceContainer(probe);
        var container = ChooseCompatibleContainer(sourceContainer, videoCodec, audioCodec);
        var containerMatched = sourceContainer is not null && container == sourceContainer;
        if (container == ExportContainer.WebM)
        {
            videoMatched &= videoCodec == VideoCodecFamily.Vp9;
            audioMatched &= audio is null || audioCodec == AudioCodecFamily.Opus;
            videoCodec = VideoCodecFamily.Vp9;
            audioCodec = AudioCodecFamily.Opus;
        }
        else if (container == ExportContainer.Mp4)
        {
            videoMatched &= videoCodec == VideoCodecFamily.H264;
            audioMatched &= audio is null || audioCodec == AudioCodecFamily.Aac;
            videoCodec = VideoCodecFamily.H264;
            audioCodec = AudioCodecFamily.Aac;
        }

        var frameRate = NormalizeFrameRate(video.AverageFrameRate ?? video.NominalFrameRate);
        var videoBitRate = NormalizeBitRate(
            video.BitRateBitsPerSecond ?? EstimateVideoBitRate(probe, audio),
            MaximumVideoBitRate);
        var audioBitRate = audioMatched
            ? NormalizeBitRate(audio?.BitRateBitsPerSecond, MaximumAudioBitRate)
            : null;
        var fallbackParts = new List<string>();
        if (!videoMatched)
        {
            fallbackParts.Add("video codec");
        }
        if (!audioMatched)
        {
            fallbackParts.Add("audio codec");
        }
        if (!containerMatched)
        {
            fallbackParts.Add("container");
        }

        var usedFallback = fallbackParts.Count > 0;
        var displayName = $"Match input — {ContainerLabel(container)} · " +
                          $"{VideoCodecLabel(videoCodec)}/{AudioCodecLabel(audioCodec)}" +
                          (usedFallback ? " (fallback)" : string.Empty);
        var preset = new ExportPreset(
            "resolved-match-input-v1",
            displayName,
            FileExtension(container),
            container,
            videoCodec,
            audioCodec,
            requiresEvenDimensions: true,
            parameterMode: ExportParameterMode.Fixed,
            frameRate,
            videoBitRate,
            audioBitRate);
        var explanation = usedFallback
            ? $"Matched available source parameters; {string.Join(", ", fallbackParts)} used a supported fallback. Crop still sets output resolution."
            : "Matched source container, codecs, frame rate, and available stream bitrates. Crop still sets output resolution.";
        return new MatchInputExportResolution(preset, usedFallback, explanation);
    }

    private static bool TryMapVideoCodec(string codecName, out VideoCodecFamily codec)
    {
        if (string.Equals(codecName, "h264", StringComparison.OrdinalIgnoreCase))
        {
            codec = VideoCodecFamily.H264;
            return true;
        }
        if (string.Equals(codecName, "vp9", StringComparison.OrdinalIgnoreCase))
        {
            codec = VideoCodecFamily.Vp9;
            return true;
        }

        codec = default;
        return false;
    }

    private static bool TryMapAudioCodec(string codecName, out AudioCodecFamily codec)
    {
        if (string.Equals(codecName, "aac", StringComparison.OrdinalIgnoreCase))
        {
            codec = AudioCodecFamily.Aac;
            return true;
        }
        if (string.Equals(codecName, "opus", StringComparison.OrdinalIgnoreCase))
        {
            codec = AudioCodecFamily.Opus;
            return true;
        }

        codec = default;
        return false;
    }

    private static ExportContainer? InferSourceContainer(MediaProbeResult probe)
    {
        ExportContainer? fromExtension = Path.GetExtension(probe.SourcePath).ToLowerInvariant() switch
        {
            ".mp4" or ".mov" or ".m4v" => ExportContainer.Mp4,
            ".webm" => ExportContainer.WebM,
            ".mkv" => ExportContainer.Matroska,
            _ => null,
        };
        if (fromExtension is not null)
        {
            return fromExtension;
        }

        var formatName = probe.FormatName;
        if (formatName.Contains("webm", StringComparison.OrdinalIgnoreCase)) return ExportContainer.WebM;
        if (formatName.Contains("matroska", StringComparison.OrdinalIgnoreCase)) return ExportContainer.Matroska;
        if (formatName.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
            formatName.Contains("mov", StringComparison.OrdinalIgnoreCase)) return ExportContainer.Mp4;
        return null;
    }

    private static ExportContainer ChooseCompatibleContainer(
        ExportContainer? sourceContainer,
        VideoCodecFamily videoCodec,
        AudioCodecFamily audioCodec)
    {
        if (sourceContainer == ExportContainer.Matroska)
        {
            return ExportContainer.Matroska;
        }
        if (sourceContainer == ExportContainer.Mp4 &&
            videoCodec == VideoCodecFamily.H264 && audioCodec == AudioCodecFamily.Aac)
        {
            return ExportContainer.Mp4;
        }
        if (sourceContainer == ExportContainer.WebM &&
            videoCodec == VideoCodecFamily.Vp9 && audioCodec == AudioCodecFamily.Opus)
        {
            return ExportContainer.WebM;
        }

        return videoCodec == VideoCodecFamily.Vp9
            ? ExportContainer.WebM
            : ExportContainer.Mp4;
    }

    private static FrameRate? NormalizeFrameRate(FrameRate? frameRate) =>
        frameRate is { IsZero: false } value && value.FramesPerSecond <= 240
            ? value
            : null;

    private static long? EstimateVideoBitRate(MediaProbeResult probe, AudioStreamInfo? audio)
    {
        if (probe.BitRateBitsPerSecond is not { } total)
        {
            return null;
        }

        var estimate = total - (audio?.BitRateBitsPerSecond ?? 0);
        return estimate > 0 ? estimate : total;
    }

    private static long? NormalizeBitRate(long? value, long maximum) =>
        value is > 0 and <= long.MaxValue ? Math.Min(value.Value, maximum) : null;

    private static string FileExtension(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => ".mp4",
        ExportContainer.WebM => ".webm",
        ExportContainer.Matroska => ".mkv",
        _ => throw new ArgumentOutOfRangeException(nameof(container)),
    };

    private static string ContainerLabel(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => "MP4",
        ExportContainer.WebM => "WebM",
        ExportContainer.Matroska => "MKV",
        _ => throw new ArgumentOutOfRangeException(nameof(container)),
    };

    private static string VideoCodecLabel(VideoCodecFamily codec) => codec switch
    {
        VideoCodecFamily.H264 => "H.264",
        VideoCodecFamily.Vp9 => "VP9",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    private static string AudioCodecLabel(AudioCodecFamily codec) => codec switch
    {
        AudioCodecFamily.Aac => "AAC",
        AudioCodecFamily.Opus => "Opus",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}
