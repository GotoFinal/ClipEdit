using System.Collections.Immutable;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.InternetMedia;

internal static class InternetMediaPreparedImportFactory
{
    public static ImportedMedia Create(InternetMediaPreparedImport prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var request = prepared.Request;
        var duration = ToMediaTime(request.Info.Duration) ??
                       throw new InternetMediaException(
                           "This link does not report a duration, so it must finish downloading before editing.");
        var video = SelectVideoFormat(request);
        if (video?.Width is not > 0 || video.Height is not > 0)
        {
            throw new InternetMediaException(
                "This link does not report a usable video size, so it must finish downloading before editing.");
        }

        var streams = ImmutableArray.CreateBuilder<MediaStreamInfo>();
        streams.Add(new VideoStreamInfo(
            0,
            NormalizeVideoCodec(video.VideoCodec),
            null,
            null,
            null,
            null,
            true,
            false,
            null,
            MediaTime.Zero,
            duration,
            new PixelSize(video.Width.Value, video.Height.Value),
            0,
            ToFrameRate(video.FrameRate),
            ToFrameRate(video.FrameRate),
            null,
            "1:1",
            null,
            null,
            null,
            null,
            null,
            "progressive"));

        var audio = SelectAudioFormat(request);
        if (audio is not null)
        {
            streams.Add(new AudioStreamInfo(
                1,
                NormalizeAudioCodec(audio.AudioCodec),
                null,
                null,
                null,
                null,
                true,
                false,
                null,
                MediaTime.Zero,
                duration,
                null,
                null,
                null,
                null,
                audio.AudioBitrateKbps is > 0
                    ? checked((long)Math.Round(audio.AudioBitrateKbps.Value * 1_000))
                    : null));
        }

        var chapters = request.Info.Chapters.IsDefault
            ? ImmutableArray<MediaChapterInfo>.Empty
            : request.Info.Chapters.Select(static chapter => new MediaChapterInfo(
                    chapter.Title,
                    new MediaRange(ToMediaTime(chapter.Start)!.Value, ToMediaTime(chapter.End)!.Value)))
                .ToImmutableArray();
        var probe = new MediaProbeResult(
            prepared.ExpectedLocalPath,
            "matroska",
            "Matroska internet editing copy",
            MediaTime.Zero,
            duration,
            null,
            null,
            streams.ToImmutable(),
            chapters);
        return new ImportedMedia(request.Info.Title, probe);
    }

    private static InternetMediaFormat? SelectVideoFormat(InternetMediaDownloadRequest request)
    {
        var limit = request.VideoQuality.MaximumHeight;
        var candidates = request.Info.Formats.Where(static format => format.HasVideo);
        if (limit is { } maximumHeight)
        {
            var limited = candidates.Where(format => format.Height is > 0 && format.Height <= maximumHeight);
            if (limited.Any())
            {
                candidates = limited;
            }
        }

        return candidates
            .OrderByDescending(static format => format.Height ?? 0)
            .ThenByDescending(static format => format.Width ?? 0)
            .ThenByDescending(static format => format.FrameRate ?? 0)
            .ThenByDescending(static format => format.FileSizeBytes ?? 0)
            .FirstOrDefault();
    }

    private static InternetMediaFormat? SelectAudioFormat(InternetMediaDownloadRequest request)
    {
        var limit = request.AudioQuality.MaximumBitrateKbps;
        var candidates = request.Info.Formats.Where(static format => format.HasAudio);
        if (limit is { } maximumBitrate)
        {
            var limited = candidates.Where(format =>
                format.AudioBitrateKbps is > 0 && format.AudioBitrateKbps <= maximumBitrate);
            if (limited.Any())
            {
                candidates = limited;
            }
        }

        return candidates
            .OrderByDescending(static format => !format.HasVideo)
            .ThenByDescending(static format => format.AudioBitrateKbps ?? 0)
            .ThenByDescending(static format => format.FileSizeBytes ?? 0)
            .FirstOrDefault();
    }

    private static string NormalizeVideoCodec(string? codec) => codec?.ToLowerInvariant() switch
    {
        { } value when value.StartsWith("avc1", StringComparison.Ordinal) => "h264",
        { } value when value.StartsWith("vp09", StringComparison.Ordinal) => "vp9",
        { } value when value.StartsWith("av01", StringComparison.Ordinal) => "av1",
        { } value when value.StartsWith("hev1", StringComparison.Ordinal) ||
                       value.StartsWith("hvc1", StringComparison.Ordinal) => "hevc",
        { Length: > 0 } value => value,
        _ => "unknown",
    };

    private static string NormalizeAudioCodec(string? codec) => codec?.ToLowerInvariant() switch
    {
        { } value when value.StartsWith("mp4a", StringComparison.Ordinal) => "aac",
        { Length: > 0 } value => value,
        _ => "unknown",
    };

    private static FrameRate? ToFrameRate(double? framesPerSecond)
    {
        if (framesPerSecond is not (> 0 and <= 1_000))
        {
            return null;
        }

        return new FrameRate(
            checked((long)Math.Round(framesPerSecond.Value * 1_000)),
            1_000);
    }

    private static MediaTime? ToMediaTime(TimeSpan? value)
    {
        if (value is null || value < TimeSpan.Zero)
        {
            return null;
        }

        return new MediaTime(value.Value.Ticks, checked((int)TimeSpan.TicksPerSecond));
    }
}
