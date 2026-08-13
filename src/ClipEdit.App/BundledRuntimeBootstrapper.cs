namespace ClipEdit.App;

internal static class BundledRuntimeBootstrapper
{
    internal const string FfmpegEnvironmentVariable = "CLIPEDIT_FFMPEG_PATH";
    internal const string FfprobeEnvironmentVariable = "CLIPEDIT_FFPROBE_PATH";
    internal const string LibMpvEnvironmentVariable = "CLIPEDIT_LIBMPV_PATH";

    public static void Prepare(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var layout = Discover(baseDirectory, OperatingSystem.IsWindows());
        SetBundledDefault(FfmpegEnvironmentVariable, layout.FfmpegPath);
        SetBundledDefault(FfprobeEnvironmentVariable, layout.FfprobePath);
        SetBundledDefault(LibMpvEnvironmentVariable, layout.LibMpvPath);

        if (OperatingSystem.IsLinux())
        {
            EnsureExecutable(layout.FfmpegPath);
            EnsureExecutable(layout.FfprobePath);
        }
    }

    internal static BundledRuntimeLayout Discover(string baseDirectory, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var executableSuffix = isWindows ? ".exe" : string.Empty;
        return new BundledRuntimeLayout(
            ExistingFile(Path.Combine(baseDirectory, "tools", "ffmpeg", $"ffmpeg{executableSuffix}")),
            ExistingFile(Path.Combine(baseDirectory, "tools", "ffmpeg", $"ffprobe{executableSuffix}")),
            ExistingFile(Path.Combine(baseDirectory, isWindows ? "libmpv-2.dll" : "libmpv.so.2")));
    }

    private static string? ExistingFile(string path) =>
        File.Exists(path) ? Path.GetFullPath(path) : null;

    private static void SetBundledDefault(string variableName, string? path)
    {
        if (path is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName)))
        {
            Environment.SetEnvironmentVariable(variableName, path);
        }
    }

    private static void EnsureExecutable(string? path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The bundle extractor normally preserves executable mode. If the
            // extraction location is read-only, process startup reports the
            // actionable error when FFmpeg is first used.
        }
    }
}

internal sealed record BundledRuntimeLayout(
    string? FfmpegPath,
    string? FfprobePath,
    string? LibMpvPath);
