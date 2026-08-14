namespace ClipEdit.App;

internal static class BundledRuntimeBootstrapper
{
    internal const string BundledFfmpegEnvironmentVariable = "CLIPEDIT_BUNDLED_FFMPEG_PATH";
    internal const string BundledFfprobeEnvironmentVariable = "CLIPEDIT_BUNDLED_FFPROBE_PATH";
    internal const string BundledLibMpvEnvironmentVariable = "CLIPEDIT_BUNDLED_LIBMPV_PATH";

    public static void Prepare(string baseDirectory) =>
        Prepare(baseDirectory, OperatingSystem.IsWindows(), OperatingSystem.IsLinux());

    internal static void Prepare(string baseDirectory, bool isWindows, bool isLinux)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var layout = Discover(baseDirectory, isWindows);
        SetBundledDefault(BundledFfmpegEnvironmentVariable, layout.FfmpegPath);
        SetBundledDefault(BundledFfprobeEnvironmentVariable, layout.FfprobePath);
        SetBundledDefault(BundledLibMpvEnvironmentVariable, layout.LibMpvPath);

        if (isLinux && OperatingSystem.IsLinux())
        {
            EnsureExecutable(layout.FfmpegPath);
            EnsureExecutable(layout.FfprobePath);
        }
    }

    internal static BundledRuntimeLayout Discover(string baseDirectory, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var executableSuffix = isWindows ? ".exe" : string.Empty;
        var toolDirectory = Path.Combine(baseDirectory, "tools", "ffmpeg");
        var libMpvName = isWindows ? "libmpv-2.dll" : "libmpv.so.2";
        var libMpvPath = isWindows
            ? ExistingFile(Path.Combine(toolDirectory, libMpvName)) ??
              ExistingFile(Path.Combine(baseDirectory, libMpvName))
            : ExistingFile(Path.Combine(baseDirectory, libMpvName));
        return new BundledRuntimeLayout(
            ExistingFile(Path.Combine(toolDirectory, $"ffmpeg{executableSuffix}")),
            ExistingFile(Path.Combine(toolDirectory, $"ffprobe{executableSuffix}")),
            libMpvPath);
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
