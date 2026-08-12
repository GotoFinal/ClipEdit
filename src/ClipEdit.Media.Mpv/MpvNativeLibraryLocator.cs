namespace ClipEdit.Media.Mpv;

public static class MpvNativeLibraryLocator
{
    private const string WindowsReleaseTag = "2026-08-11-f4d13e1c2c";

    public static string? Find(string? explicitPath = null)
    {
        var libraryName = OperatingSystem.IsWindows()
            ? "libmpv-2.dll"
            : "libmpv.so.2";

        var candidates = new List<string?>
        {
            explicitPath,
            Environment.GetEnvironmentVariable("CLIPEDIT_LIBMPV_PATH"),
            Path.Combine(AppContext.BaseDirectory, libraryName),
        };

        if (OperatingSystem.IsWindows())
        {
            var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
            if (repositoryRoot is not null)
            {
                candidates.Add(
                    Path.Combine(
                        repositoryRoot,
                        "packages",
                        "native",
                        "libmpv",
                        "win-x64",
                        WindowsReleaseTag,
                        "runtime",
                        libraryName));
            }
        }

        return candidates.Select(TryResolve).FirstOrDefault(path => path is not null);
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
