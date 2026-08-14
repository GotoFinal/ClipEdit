namespace ClipEdit.Media.Mpv;

public static class MpvNativeLibraryLocator
{
    private const string WindowsReleaseTag = "2026-08-11-f4d13e1c2c";
    private const string BundledLibMpvVariable = "CLIPEDIT_BUNDLED_LIBMPV_PATH";

    public static string? Find(string? explicitPath = null, bool preferSystem = false)
    {
        var libraryName = OperatingSystem.IsWindows()
            ? "libmpv-2.dll"
            : "libmpv.so.2";
        var overridePath = Environment.GetEnvironmentVariable("CLIPEDIT_LIBMPV_PATH");
        if (OperatingSystem.IsLinux() &&
            (string.Equals(explicitPath, libraryName, StringComparison.Ordinal) ||
             string.Equals(overridePath, libraryName, StringComparison.Ordinal)) &&
            TryResolveSystemLibrary(libraryName) is { } explicitSystemLibrary)
        {
            return explicitSystemLibrary;
        }

        var bundledCandidates = new List<string?>
        {
            Environment.GetEnvironmentVariable(BundledLibMpvVariable),
            Path.Combine(AppContext.BaseDirectory, libraryName),
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
                        "libmpv",
                        "win-x64",
                        WindowsReleaseTag,
                        "runtime",
                        libraryName));
            }
        }

        return FindCore(
            explicitPath,
            overridePath,
            bundledCandidates,
            OperatingSystem.IsLinux() ? libraryName : null,
            preferSystem,
            TryResolveSystemLibrary);
    }

    internal static string? FindCore(
        string? explicitPath,
        string? overridePath,
        IEnumerable<string?> bundledCandidates,
        string? systemLibraryName,
        bool preferSystem,
        Func<string, string?> systemResolver)
    {
        var manual = TryResolve(explicitPath) ?? TryResolve(overridePath);
        if (manual is not null)
        {
            return manual;
        }

        if (preferSystem && systemLibraryName is not null &&
            systemResolver(systemLibraryName) is { } preferredSystem)
        {
            return preferredSystem;
        }

        var bundled = bundledCandidates
            .Select(TryResolve)
            .FirstOrDefault(path => path is not null);
        if (bundled is not null)
        {
            return bundled;
        }

        return !preferSystem && systemLibraryName is not null
            ? systemResolver(systemLibraryName)
            : null;
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

    private static string? TryResolveSystemLibrary(string libraryName)
    {
        try
        {
            using var library = Native.MpvNativeLibrary.Load(libraryName);
            return libraryName;
        }
        catch (Exception exception) when (
            exception is MpvPreviewException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
