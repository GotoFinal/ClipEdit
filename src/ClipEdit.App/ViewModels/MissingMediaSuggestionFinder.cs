using ClipEdit.Application.Projects;

namespace ClipEdit.App.ViewModels;

internal static class MissingMediaSuggestionFinder
{
    private const int MaximumDirectoryDepth = 6;
    private const int MaximumVisitedDirectories = 512;

    public static string? FindSuggestion(
        string projectPath,
        ProjectMediaDocument savedMedia,
        IReadOnlySet<string>? excludedPaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(savedMedia);

        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            var fileName = SplitPath(savedMedia.SourcePath).LastOrDefault();
            if (string.IsNullOrWhiteSpace(projectDirectory) ||
                string.IsNullOrWhiteSpace(fileName) ||
                !Directory.Exists(projectDirectory))
            {
                return null;
            }

            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var candidates = EnumerateNearbyMatches(projectDirectory, fileName, cancellationToken)
                .Where(path => excludedPaths is null || !excludedPaths.Contains(path))
                .Where(path => HasExpectedSize(path, savedMedia.ExpectedFileSizeBytes))
                .Select(path => new RankedCandidate(
                    path,
                    CountMatchingPathSuffix(savedMedia.SourcePath, Path.GetRelativePath(projectDirectory, path))))
                .OrderByDescending(candidate => candidate.MatchingSuffixSegments)
                .ThenBy(candidate => candidate.Path, pathComparer)
                .ToArray();
            if (candidates.Length == 0)
            {
                return null;
            }

            var bestScore = candidates[0].MatchingSuffixSegments;
            return candidates.Count(candidate => candidate.MatchingSuffixSegments == bestScore) == 1
                ? candidates[0].Path
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or
                IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateNearbyMatches(
        string projectDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var directories = new Queue<(string Path, int Depth)>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        directories.Enqueue((projectDirectory, 0));

        while (directories.Count > 0 && visited.Count < MaximumVisitedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = directories.Dequeue();
            if (!visited.Add(directory))
            {
                continue;
            }

            string? candidate = null;
            try
            {
                var possibleCandidate = Path.GetFullPath(Path.Combine(directory, fileName));
                candidate = File.Exists(possibleCandidate) ? possibleCandidate : null;
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException or
                    IOException or UnauthorizedAccessException)
            {
            }

            if (candidate is not null)
            {
                yield return candidate;
            }

            if (depth >= MaximumDirectoryDepth)
            {
                continue;
            }

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory)
                    .Where(path => !HasReparsePoint(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or PathTooLongException)
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                directories.Enqueue((child, depth + 1));
            }
        }
    }

    private static bool HasExpectedSize(string path, long? expectedSize)
    {
        if (expectedSize is null)
        {
            return true;
        }

        try
        {
            return new FileInfo(path).Length == expectedSize.Value;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    internal static int CountMatchingPathSuffix(string originalPath, string candidateRelativePath)
    {
        var original = SplitPath(originalPath);
        var candidate = SplitPath(candidateRelativePath);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var count = 0;
        while (count < original.Length && count < candidate.Length &&
               comparer.Equals(original[^(count + 1)], candidate[^(count + 1)]))
        {
            count++;
        }

        return count;
    }

    private static string[] SplitPath(string path) =>
        path.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record RankedCandidate(string Path, int MatchingSuffixSegments);
}
