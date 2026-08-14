namespace ClipEdit.Media.FFmpeg.Process;

public static class FfmpegToolLocator
{
    private const string WindowsFfmpegVersion = "9.0.1";
    private const string BundledFfmpegVariable = "CLIPEDIT_BUNDLED_FFMPEG_PATH";
    private const string BundledFfprobeVariable = "CLIPEDIT_BUNDLED_FFPROBE_PATH";

    public static string? FindFfmpeg(string? explicitPath = null, bool preferSystem = false)
    {
        return Find(
            "ffmpeg",
            "CLIPEDIT_FFMPEG_PATH",
            BundledFfmpegVariable,
            explicitPath,
            preferSystem);
    }

    public static string? FindFfprobe(string? explicitPath = null, bool preferSystem = false)
    {
        return Find(
            "ffprobe",
            "CLIPEDIT_FFPROBE_PATH",
            BundledFfprobeVariable,
            explicitPath,
            preferSystem);
    }

    private static string? Find(
        string toolName,
        string overrideVariable,
        string bundledVariable,
        string? explicitPath,
        bool preferSystem)
    {
        var executableName = OperatingSystem.IsWindows() ? $"{toolName}.exe" : toolName;
        var bundledCandidates = new List<string?>
        {
            Environment.GetEnvironmentVariable(bundledVariable),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", executableName),
        };

        if (OperatingSystem.IsWindows())
        {
            var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
            if (repositoryRoot is not null)
            {
                bundledCandidates.Add(
                    Path.Combine(
                        repositoryRoot,
                        "packages",
                        "native",
                        "ffmpeg",
                        "win-x64",
                        WindowsFfmpegVersion,
                        "runtime",
                        $"ffmpeg-{WindowsFfmpegVersion}-full_build",
                        "bin",
                        executableName));
            }
        }

        var systemCandidates = new List<string?>();
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            systemCandidates.AddRange(
                pathVariable
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(directory => Path.Combine(directory, executableName)));
        }

        return FindCore(
            explicitPath,
            Environment.GetEnvironmentVariable(overrideVariable),
            bundledCandidates,
            systemCandidates,
            preferSystem);
    }

    internal static string? FindCore(
        string? explicitPath,
        string? overridePath,
        IEnumerable<string?> bundledCandidates,
        IEnumerable<string?> systemCandidates,
        bool preferSystem)
    {
        var candidates = new List<string?>
        {
            explicitPath,
            overridePath,
        };
        if (preferSystem)
        {
            candidates.AddRange(systemCandidates);
        }
        candidates.AddRange(bundledCandidates);
        if (!preferSystem)
        {
            candidates.AddRange(systemCandidates);
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

    private static string? FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClipEdit.sln")))
            {
                return directory.FullName;
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
