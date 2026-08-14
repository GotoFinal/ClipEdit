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
        PixelSize maximumSize,
        bool toneMapHdr = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (timestamp < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        var timestampText = timestamp.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture);
        var colorFilter = toneMapHdr
            ? "zscale=transfer=linear:npl=100,format=gbrpf32le," +
              "zscale=primaries=bt709,tonemap=mobius:desat=0," +
              "zscale=transfer=iec61966-2-1:matrix=gbr:range=pc,format=rgb24,"
            : string.Empty;
        var scaleFilter = colorFilter +
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
