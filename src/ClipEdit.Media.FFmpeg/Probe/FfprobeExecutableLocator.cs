namespace ClipEdit.Media.FFmpeg.Probe;

public static class FfprobeExecutableLocator
{
    private const string OverrideVariable = "CLIPEDIT_FFPROBE_PATH";

    public static string? Find(string? explicitPath = null)
    {
        var candidates = new List<string?>
        {
            explicitPath,
            Environment.GetEnvironmentVariable(OverrideVariable),
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "ffmpeg",
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"),
        };

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            var executableName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
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
