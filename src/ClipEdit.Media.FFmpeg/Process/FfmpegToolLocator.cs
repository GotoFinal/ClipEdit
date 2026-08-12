namespace ClipEdit.Media.FFmpeg.Process;

public static class FfmpegToolLocator
{
    public static string? FindFfmpeg(string? explicitPath = null)
    {
        return Find(
            "ffmpeg",
            "CLIPEDIT_FFMPEG_PATH",
            explicitPath);
    }

    public static string? FindFfprobe(string? explicitPath = null)
    {
        return Find(
            "ffprobe",
            "CLIPEDIT_FFPROBE_PATH",
            explicitPath);
    }

    private static string? Find(
        string toolName,
        string overrideVariable,
        string? explicitPath)
    {
        var executableName = OperatingSystem.IsWindows() ? $"{toolName}.exe" : toolName;
        var candidates = new List<string?>
        {
            explicitPath,
            Environment.GetEnvironmentVariable(overrideVariable),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", executableName),
        };

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            candidates.AddRange(
                pathVariable
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(directory => Path.Combine(directory, executableName)));
        }

        foreach (var candidate in candidates)
        {
            var resolved = TryResolve(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? TryResolve(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
