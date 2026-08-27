using System.Security.Cryptography;
using System.Text;

namespace ClipEdit.App.InternetMedia;

internal sealed class InternetMediaCache
{
    private const string CacheFormatVersion = "3-streaming-preview";
    private readonly string _cacheRoot;

    public InternetMediaCache(string cacheRoot)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public string GetEntryDirectory(InternetMediaDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = $"{CacheFormatVersion}\n" +
                       $"{request.Info.SourceUri.AbsoluteUri}\n" +
                       $"{request.VideoQuality.MaximumHeight?.ToString() ?? "best"}\n" +
                       $"{request.AudioQuality.MaximumBitrateKbps?.ToString() ?? "best"}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        var directory = Path.GetFullPath(Path.Combine(_cacheRoot, key));
        if (!IsWithin(directory, _cacheRoot))
        {
            throw new InternetMediaException("The internet-media cache path is invalid.");
        }
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string? TryGetCompletedPath(string entryDirectory)
    {
        var markerPath = Path.Combine(entryDirectory, "complete.txt");
        try
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }
            var fileName = File.ReadAllText(markerPath).Trim();
            if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName)
            {
                return null;
            }
            var path = Path.GetFullPath(Path.Combine(entryDirectory, fileName));
            return IsWithin(path, entryDirectory) && new FileInfo(path) is { Exists: true, Length: > 0 }
                ? path
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public string GetExpectedCompletedPath(InternetMediaDownloadRequest request) =>
        Path.Combine(GetEntryDirectory(request), "media.mkv");

    public string MarkCompleted(string entryDirectory)
    {
        var selected = Path.Combine(entryDirectory, "media.mkv");
        if (new FileInfo(selected) is not { Exists: true, Length: > 0 })
        {
            throw new InternetMediaException("yt-dlp did not produce the expected Matroska editing copy.");
        }

        if (!IsWithin(Path.GetFullPath(selected), Path.GetFullPath(entryDirectory)))
        {
            throw new InternetMediaException("The completed internet-media cache path is invalid.");
        }
        var markerPath = Path.Combine(entryDirectory, "complete.txt");
        var temporaryPath = $"{markerPath}.{Guid.NewGuid():N}.saving";
        try
        {
            File.WriteAllText(temporaryPath, Path.GetFileName(selected));
            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
        return selected;
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathFullyQualified(relative);
    }
}
