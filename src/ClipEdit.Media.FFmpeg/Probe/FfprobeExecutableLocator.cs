namespace ClipEdit.Media.FFmpeg.Probe;

public static class FfprobeExecutableLocator
{
    public static string? Find(string? explicitPath = null, bool preferSystem = false)
    {
        return Process.FfmpegToolLocator.FindFfprobe(explicitPath, preferSystem);
    }
}
