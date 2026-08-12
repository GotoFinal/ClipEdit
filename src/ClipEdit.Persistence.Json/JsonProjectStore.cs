using System.Text.Json;
using System.Text.Json.Serialization;
using ClipEdit.Application.Projects;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Persistence.Json;

public sealed class JsonProjectStore : IProjectStore
{
    private const long MaximumProjectBytes = 4 * 1024 * 1024;
    private const int MaximumMediaItems = 10_000;
    private const int MaximumRangesPerMedia = 100_000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<ProjectDocument> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(projectPath);
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumProjectBytes)
            {
                throw new ProjectStoreException(
                    ProjectStoreFailure.TooLarge,
                    "The project file exceeds the 4 MiB safety limit.");
            }

            var document = await JsonSerializer.DeserializeAsync<ProjectDocument>(
                stream,
                SerializerOptions,
                cancellationToken);
            return Validate(document);
        }
        catch (ProjectStoreException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new ProjectStoreException(ProjectStoreFailure.NotFound, "The project file was not found.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProjectStoreException(ProjectStoreFailure.AccessDenied, "The project file cannot be accessed.", exception);
        }
        catch (JsonException exception)
        {
            throw new ProjectStoreException(
                ProjectStoreFailure.InvalidDocument,
                "The project file is malformed or contains unsupported fields.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ProjectStoreException(ProjectStoreFailure.IoFailure, "The project file could not be read.", exception);
        }
    }

    public async Task SaveAsync(
        string projectPath,
        ProjectDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        var fullPath = NormalizePath(projectPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new ProjectStoreException(
                ProjectStoreFailure.IoFailure,
                "The selected project folder does not exist.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.saving");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                if (stream.Length > MaximumProjectBytes)
                {
                    throw new ProjectStoreException(
                        ProjectStoreFailure.TooLarge,
                        "The project exceeds the 4 MiB safety limit.");
                }
            }

            if (File.Exists(fullPath))
            {
                try
                {
                    File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporaryPath, fullPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, fullPath, overwrite: false);
            }
        }
        catch (ProjectStoreException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            TryDelete(temporaryPath);
            throw new ProjectStoreException(ProjectStoreFailure.AccessDenied, "The project file cannot be saved.", exception);
        }
        catch (IOException exception)
        {
            TryDelete(temporaryPath);
            throw new ProjectStoreException(ProjectStoreFailure.IoFailure, "The project file could not be saved.", exception);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static ProjectDocument Validate(ProjectDocument? document)
    {
        if (document is null)
        {
            throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "The project document is empty.");
        }

        if (document.SchemaVersion != ProjectDocument.CurrentSchemaVersion)
        {
            throw new ProjectStoreException(
                ProjectStoreFailure.UnsupportedVersion,
                $"Project schema {document.SchemaVersion} is not supported by this version of ClipEdit.");
        }

        if (document.ProjectId == Guid.Empty || document.Media is null)
        {
            throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "The project identity or media list is invalid.");
        }

        if (document.Media.Count > MaximumMediaItems)
        {
            throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "The project contains too many media items.");
        }

        foreach (var media in document.Media)
        {
            ValidateMedia(media);
        }

        return document;
    }

    private static void ValidateMedia(ProjectMediaDocument? media)
    {
        if (media is null ||
            string.IsNullOrWhiteSpace(media.SourcePath) ||
            media.SourcePath.Length > 32_768 ||
            !Path.IsPathFullyQualified(media.SourcePath) ||
            media.ExpectedFileSizeBytes < 0 ||
            media.SourceWidth <= 0 ||
            media.SourceHeight <= 0 ||
            media.CropX < 0 ||
            media.CropY < 0 ||
            media.CropWidth <= 0 ||
            media.CropHeight <= 0 ||
            media.CropWidth > media.SourceWidth - media.CropX ||
            media.CropHeight > media.SourceHeight - media.CropY ||
            media.SourceDurationNumerator <= 0 ||
            media.SourceDurationDenominator <= 0 ||
            media.KeptRanges is null ||
            media.KeptRanges.Count > MaximumRangesPerMedia)
        {
            throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "A media entry contains invalid values.");
        }

        try
        {
            var sourceDuration = new MediaTime(
                media.SourceDurationNumerator,
                media.SourceDurationDenominator);
            var ranges = media.KeptRanges.Select(range => new MediaRange(
                new MediaTime(range.StartNumerator, range.StartDenominator),
                new MediaTime(range.EndNumerator, range.EndDenominator)));
            _ = SourceEdit.FromKeptRanges(sourceDuration, ranges);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or DivideByZeroException)
        {
            throw new ProjectStoreException(
                ProjectStoreFailure.InvalidDocument,
                "A saved edit range is invalid.",
                exception);
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leave only the private sibling temp if the filesystem refuses cleanup.
        }
    }
}
