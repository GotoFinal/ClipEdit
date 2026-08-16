namespace ClipEdit.Media.FFmpeg.Probe;

internal static class FfprobeKeyframeArguments
{
    public static IReadOnlyList<string> Create(string sourcePath, int videoStreamIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);

        return
        [
            "-hide_banner",
            "-v",
            "error",
            "-select_streams",
            videoStreamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-skip_frame",
            "nokey",
            "-show_frames",
            "-show_entries",
            "frame=best_effort_timestamp_time,pkt_dts_time",
            "-print_format",
            "json",
            "--",
            sourcePath,
        ];
    }
}
