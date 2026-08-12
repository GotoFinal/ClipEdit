using System.Globalization;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.FFmpeg.Frames;

internal static class FfmpegFrameArguments
{
    public static IReadOnlyList<string> Create(
        string sourcePath,
        int videoStreamIndex,
        MediaTime timestamp,
        PixelSize maximumSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (timestamp < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        var timestampText = timestamp.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture);
        var scaleFilter =
            $"scale={maximumSize.Width}:{maximumSize.Height}:" +
            "force_original_aspect_ratio=decrease:force_divisible_by=2";

        return
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-ss",
            timestampText,
            "-i",
            sourcePath,
            "-map",
            $"0:{videoStreamIndex}",
            "-an",
            "-sn",
            "-dn",
            "-frames:v",
            "1",
            "-vf",
            scaleFilter,
            "-c:v",
            "png",
            "-f",
            "image2pipe",
            "pipe:1",
        ];
    }
}
