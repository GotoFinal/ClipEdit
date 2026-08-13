using System.Text.Json;
using System.Text.Json.Serialization;
using ClipEdit.Application.Projects;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Persistence.Json;

public sealed class JsonProjectStore : IProjectStore
{
    private const long MaximumProjectBytes = 4 * 1024 * 1024;
    private const int MaximumMediaItems = 10_000;
    private const int MaximumRangesPerMedia = 100_000;
    private const int MaximumAudioTracksPerMedia = 1_000;
    private const int MaximumVideoClips = 100_000;

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

    public Task DeleteIfExistsAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = NormalizePath(projectPath);
        try
        {
            File.Delete(fullPath);
            return Task.CompletedTask;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProjectStoreException(ProjectStoreFailure.AccessDenied, "The recovery file cannot be removed.", exception);
        }
        catch (IOException exception)
        {
            throw new ProjectStoreException(ProjectStoreFailure.IoFailure, "The recovery file cannot be removed.", exception);
        }
    }

    private static ProjectDocument Validate(ProjectDocument? document)
    {
        if (document is null)
        {
            throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "The project document is empty.");
        }

        if (document.SchemaVersion is < 1 or > ProjectDocument.CurrentSchemaVersion)
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

        if (document.SchemaVersion >= 2)
        {
            ValidateSequence(document);
        }

        return document;
    }

    private static void ValidateSequence(ProjectDocument document)
    {
        if (document.VideoClips is null ||
            document.VideoClips.Count > MaximumVideoClips ||
            document.CropSettings is null ||
            string.IsNullOrWhiteSpace(document.CropSettings.PresetId) ||
            document.Media.Any(media => media.MediaId == Guid.Empty) ||
            document.Media.Select(media => media.MediaId).Distinct().Count() != document.Media.Count)
        {
            throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "The saved sequence metadata is invalid.");
        }

        var mediaById = document.Media.ToDictionary(media => media.MediaId);
        var clipIds = new HashSet<Guid>();
        foreach (var clip in document.VideoClips)
        {
            if (clip is null ||
                clip.ClipId == Guid.Empty ||
                !clipIds.Add(clip.ClipId) ||
                !mediaById.TryGetValue(clip.SourceMediaId, out var media) ||
                clip.SourceStartDenominator <= 0 ||
                clip.SourceEndDenominator <= 0 ||
                clip.AvailableStartDenominator <= 0 ||
                clip.AvailableEndDenominator <= 0 ||
                (document.SchemaVersion >= 5 &&
                 (clip.TimelineStartNumerator < 0 || clip.TimelineStartDenominator <= 0)))
            {
                throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "A saved video clip is invalid.");
            }

            try
            {
                var sourceRange = new MediaRange(
                    new MediaTime(clip.SourceStartNumerator, clip.SourceStartDenominator),
                    new MediaTime(clip.SourceEndNumerator, clip.SourceEndDenominator));
                var availableRange = new MediaRange(
                    new MediaTime(clip.AvailableStartNumerator, clip.AvailableStartDenominator),
                    new MediaTime(clip.AvailableEndNumerator, clip.AvailableEndDenominator));
                var timelineStart = document.SchemaVersion >= 5
                    ? new MediaTime(clip.TimelineStartNumerator, clip.TimelineStartDenominator)
                    : MediaTime.Zero;
                _ = new SequenceClip(
                    clip.ClipId,
                    clip.SourceMediaId,
                    sourceRange,
                    availableRange,
                    timelineStart);
                _ = new CropRegion(
                    new PixelSize(media.SourceWidth, media.SourceHeight),
                    clip.SourceWindowX,
                    clip.SourceWindowY,
                    clip.SourceWindowWidth,
                    clip.SourceWindowHeight);
                var scaleX = document.SchemaVersion >= 4
                    ? clip.CanvasScaleX ?? throw new ArgumentException("The horizontal canvas scale is missing.")
                    : clip.CanvasScale;
                var scaleY = document.SchemaVersion >= 4
                    ? clip.CanvasScaleY ?? throw new ArgumentException("The vertical canvas scale is missing.")
                    : clip.CanvasScale;
                _ = new ClipCanvasTransform(
                    clip.CanvasOffsetX, clip.CanvasOffsetY, scaleX, scaleY, clip.CanvasRotationDegrees);
            }
            catch (Exception exception) when (
                exception is ArgumentException or OverflowException or DivideByZeroException)
            {
                throw new ProjectStoreException(
                    ProjectStoreFailure.InvalidDocument,
                    "A saved video clip range or placement is invalid.",
                    exception);
            }
        }

        if (document.SchemaVersion >= 3)
        {
            if (document.Canvas is not { } canvas)
            {
                throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "The project canvas is missing.");
            }

            try
            {
                _ = new CropRegion(
                    new PixelSize(canvas.Width, canvas.Height),
                    canvas.CropX,
                    canvas.CropY,
                    canvas.CropWidth,
                    canvas.CropHeight);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "The project canvas is invalid.", exception);
            }
        }
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

        if (media.AudioTracks is null)
        {
            return;
        }

        if (media.AudioTracks.Count > MaximumAudioTracksPerMedia)
        {
            throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "A media entry contains too many audio tracks.");
        }

        foreach (var audioTrack in media.AudioTracks)
        {
            if (audioTrack is null ||
                audioTrack.StreamIndex < 0 ||
                !double.IsFinite(audioTrack.GainDb) ||
                audioTrack.GainDb is < -60 or > 12 ||
                audioTrack.SourceDurationNumerator <= 0 ||
                audioTrack.SourceDurationDenominator <= 0 ||
                audioTrack.TimelineOffsetNumerator < 0 ||
                audioTrack.TimelineOffsetDenominator <= 0 ||
                audioTrack.KeptRanges is null ||
                audioTrack.KeptRanges.Count > MaximumRangesPerMedia)
            {
                throw new ProjectStoreException(ProjectStoreFailure.InvalidDocument, "A saved audio track is invalid.");
            }

            try
            {
                var duration = new MediaTime(
                    audioTrack.SourceDurationNumerator,
                    audioTrack.SourceDurationDenominator);
                _ = new MediaTime(
                    audioTrack.TimelineOffsetNumerator,
                    audioTrack.TimelineOffsetDenominator);
                var ranges = audioTrack.KeptRanges.Select(range => new MediaRange(
                    new MediaTime(range.StartNumerator, range.StartDenominator),
                    new MediaTime(range.EndNumerator, range.EndDenominator)));
                _ = SourceEdit.FromKeptRanges(duration, ranges);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException or DivideByZeroException)
            {
                throw new ProjectStoreException(
                    ProjectStoreFailure.InvalidDocument,
                    "A saved audio edit range is invalid.",
                    exception);
            }
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
