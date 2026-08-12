using System.Globalization;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.FFmpeg.Analysis;

internal static class FfmpegWaveformArguments
{
    public static IReadOnlyList<string> Create(
        string sourcePath,
        int audioStreamIndex,
        MediaRange visibleRange,
        PixelSize outputSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegative(audioStreamIndex);
        if (visibleRange.IsEmpty || visibleRange.Start < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleRange));
        }

        var start = visibleRange.Start.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture);
        var duration = (visibleRange.End - visibleRange.Start).TotalSeconds
            .ToString("0.#########", CultureInfo.InvariantCulture);
        var filter =
            $"[0:{audioStreamIndex}]" +
            $"aformat=channel_layouts=mono," +
            $"showwavespic=s={outputSize.Width}x{outputSize.Height}:colors=0x9D83FF:scale=sqrt," +
            "format=rgba[waveform]";

        return
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-nostdin",
            "-ss",
            start,
            "-t",
            duration,
            "-i",
            sourcePath,
            "-filter_complex",
            filter,
            "-map",
            "[waveform]",
            "-frames:v",
            "1",
            "-c:v",
            "png",
            "-f",
            "image2pipe",
            "pipe:1",
        ];
    }
}
