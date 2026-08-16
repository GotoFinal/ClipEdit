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
            "-show_packets",
            "-show_entries",
            "packet=pts_time,dts_time,flags",
            "-print_format",
            "compact=p=0:nk=0",
            "--",
            sourcePath,
        ];
    }
}
