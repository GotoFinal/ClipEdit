using System.IO.Compression;
using System.Reflection;

namespace ClipEdit.App;

internal static class BundledRuntimeBootstrapper
{
    internal const string BundledFfmpegEnvironmentVariable = "CLIPEDIT_BUNDLED_FFMPEG_PATH";
    internal const string BundledFfprobeEnvironmentVariable = "CLIPEDIT_BUNDLED_FFPROBE_PATH";
    internal const string BundledLibMpvEnvironmentVariable = "CLIPEDIT_BUNDLED_LIBMPV_PATH";
    internal const string BundledNoticesEnvironmentVariable = "CLIPEDIT_BUNDLED_NOTICES_PATH";
    internal const string MediaArchiveResourceName = "ClipEdit.BundledMediaRuntime";
    internal const string NoticesArchiveResourceName = "ClipEdit.BundledNotices";
    internal const string MediaArchiveIdMetadataName = "ClipEditBundledMediaRuntimeId";
    internal const string NoticesArchiveIdMetadataName = "ClipEditBundledNoticesId";

    private const string CompletionMarkerName = ".complete";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);

    public static void Prepare(string baseDirectory) =>
        Prepare(baseDirectory, OperatingSystem.IsWindows(), OperatingSystem.IsLinux());

    internal static void Prepare(string baseDirectory, bool isWindows, bool isLinux)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        Advertise(Discover(baseDirectory, isWindows), isLinux);
    }

    public static BundledRuntimePreparation PrepareEmbeddedPayloads(string applicationDataDirectory) =>
        PrepareEmbeddedPayloads(
            applicationDataDirectory,
            typeof(BundledRuntimeBootstrapper).Assembly,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux());

    public static bool HasEmbeddedMediaRuntime =>
        HasResource(typeof(BundledRuntimeBootstrapper).Assembly, MediaArchiveResourceName);

    internal static BundledRuntimePreparation PrepareEmbeddedPayloads(
        string applicationDataDirectory,
        Assembly assembly,
        bool isWindows,
        bool isLinux)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentNullException.ThrowIfNull(assembly);

        var mediaId = GetMetadata(assembly, MediaArchiveIdMetadataName);
        var noticesId = GetMetadata(assembly, NoticesArchiveIdMetadataName);
        var hasMediaArchive = HasResource(assembly, MediaArchiveResourceName);
        var hasNoticesArchive = HasResource(assembly, NoticesArchiveResourceName);
        ValidateDescriptor(MediaArchiveResourceName, mediaId, hasMediaArchive);
        ValidateDescriptor(NoticesArchiveResourceName, noticesId, hasNoticesArchive);

        if (!hasMediaArchive && !hasNoticesArchive)
        {
            return BundledRuntimePreparation.NotBundled;
        }

        string? runtimeDirectory = null;
        string? noticesDirectory = null;
        var extractedRuntime = false;
        var extractedNotices = false;
        var layout = new BundledRuntimeLayout(null, null, null);
        if (hasMediaArchive)
        {
            runtimeDirectory = Path.Combine(
                applicationDataDirectory,
                "Runtime",
                NormalizeCacheKey(mediaId!));
            extractedRuntime = ExtractResourceArchive(
                assembly,
                MediaArchiveResourceName,
                mediaId!,
                runtimeDirectory,
                candidate => IsCompleteRuntime(candidate, mediaId!, isWindows));
            layout = Discover(runtimeDirectory, isWindows);
            if (!layout.IsComplete)
            {
                throw new InvalidDataException(
                    "The embedded media runtime does not contain FFmpeg, ffprobe, and libmpv.");
            }

            Advertise(layout, isLinux);
        }

        if (hasNoticesArchive)
        {
            noticesDirectory = Path.Combine(
                applicationDataDirectory,
                "Notices",
                NormalizeCacheKey(noticesId!));
            extractedNotices = ExtractResourceArchive(
                assembly,
                NoticesArchiveResourceName,
                noticesId!,
                noticesDirectory,
                candidate => HasCompletionMarker(candidate, noticesId!));
            SetBundledDefault(BundledNoticesEnvironmentVariable, noticesDirectory);
        }

        return new BundledRuntimePreparation(
            IsBundled: hasMediaArchive,
            ExtractedRuntime: extractedRuntime,
            ExtractedNotices: extractedNotices,
            RuntimeDirectory: runtimeDirectory,
            NoticesDirectory: noticesDirectory,
            Layout: layout);
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

    private static bool ExtractResourceArchive(
        Assembly assembly,
        string resourceName,
        string archiveId,
        string destinationDirectory,
        Func<string, bool> isComplete) =>
        ExtractArchiveToCache(
            () => assembly.GetManifestResourceStream(resourceName) ??
                throw new InvalidDataException($"Embedded resource '{resourceName}' is missing."),
            resourceName,
            archiveId,
            destinationDirectory,
            isComplete);

    internal static bool ExtractArchiveToCache(
        Func<Stream> openArchive,
        string archiveName,
        string archiveId,
        string destinationDirectory,
        Func<string, bool> isComplete)
    {
        ArgumentNullException.ThrowIfNull(openArchive);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(isComplete);

        if (isComplete(destinationDirectory))
        {
            return false;
        }

        var parentDirectory = Path.GetDirectoryName(destinationDirectory) ??
            throw new InvalidOperationException("The embedded payload cache has no parent directory.");
        Directory.CreateDirectory(parentDirectory);
        var lockPath = Path.Combine(parentDirectory, $".{Path.GetFileName(destinationDirectory)}.lock");
        using var extractionLock = AcquireExtractionLock(lockPath);
        if (isComplete(destinationDirectory))
        {
            return false;
        }

        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(destinationDirectory)}.staging-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            using var resource = openArchive();
            ExtractZipArchive(resource, stagingDirectory);
            File.WriteAllText(
                Path.Combine(stagingDirectory, CompletionMarkerName),
                archiveId + Environment.NewLine);
            if (!isComplete(stagingDirectory))
            {
                throw new InvalidDataException(
                    $"Embedded archive '{archiveName}' did not produce a complete payload.");
            }

            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }
            Directory.Move(stagingDirectory, destinationDirectory);
            return true;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    internal static void ExtractZipArchive(Stream archiveStream, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var extractionRoot = Path.GetFullPath(destinationDirectory);
        var extractionPrefix = extractionRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, relativePath));
            if (!destinationPath.StartsWith(extractionPrefix, comparison))
            {
                throw new InvalidDataException(
                    $"Embedded archive entry escapes its destination: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var input = entry.Open();
            using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            input.CopyTo(output);
        }
    }

    private static FileStream AcquireExtractionLock(string lockPath)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void Advertise(BundledRuntimeLayout layout, bool isLinux)
    {
        SetBundledDefault(BundledFfmpegEnvironmentVariable, layout.FfmpegPath);
        SetBundledDefault(BundledFfprobeEnvironmentVariable, layout.FfprobePath);
        SetBundledDefault(BundledLibMpvEnvironmentVariable, layout.LibMpvPath);

        if (isLinux && OperatingSystem.IsLinux())
        {
            EnsureExecutable(layout.FfmpegPath);
            EnsureExecutable(layout.FfprobePath);
        }
    }

    private static bool IsCompleteRuntime(string directory, string expectedId, bool isWindows) =>
        HasCompletionMarker(directory, expectedId) && Discover(directory, isWindows).IsComplete;

    private static bool HasCompletionMarker(string directory, string? expectedId)
    {
        var markerPath = Path.Combine(directory, CompletionMarkerName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        return expectedId is null ||
            string.Equals(File.ReadAllText(markerPath).Trim(), expectedId, StringComparison.Ordinal);
    }

    private static string NormalizeCacheKey(string value)
    {
        var normalized = new string(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '_').ToArray()).Trim('.', '_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidDataException("The embedded payload cache key is invalid.");
        }
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static void ValidateDescriptor(string resourceName, string? id, bool hasResource)
    {
        if (hasResource != !string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidDataException(
                $"Embedded resource '{resourceName}' and its cache identifier must be supplied together.");
        }
    }

    private static string? GetMetadata(Assembly assembly, string name) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, name, StringComparison.Ordinal))
            ?.Value;

    private static bool HasResource(Assembly assembly, string name) =>
        assembly.GetManifestResourceInfo(name) is not null;

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
        if (!OperatingSystem.IsLinux() || path is null)
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
            // Process startup reports the actionable error if the cache is read-only.
        }
    }
}

internal sealed record BundledRuntimeLayout(
    string? FfmpegPath,
    string? FfprobePath,
    string? LibMpvPath)
{
    public bool IsComplete => FfmpegPath is not null && FfprobePath is not null && LibMpvPath is not null;
}

internal sealed record BundledRuntimePreparation(
    bool IsBundled,
    bool ExtractedRuntime,
    bool ExtractedNotices,
    string? RuntimeDirectory,
    string? NoticesDirectory,
    BundledRuntimeLayout Layout)
{
    public static BundledRuntimePreparation NotBundled { get; } = new(
        false,
        false,
        false,
        null,
        null,
        new BundledRuntimeLayout(null, null, null));
}
