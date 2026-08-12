using ClipEdit.Media.Probe;

namespace ClipEdit.Application.Media;

public sealed class ImportMediaUseCase
{
    private readonly IMediaProbe _mediaProbe;

    public ImportMediaUseCase(IMediaProbe mediaProbe)
    {
        ArgumentNullException.ThrowIfNull(mediaProbe);
        _mediaProbe = mediaProbe;
    }

    public async Task<ImportedMedia> ExecuteAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var probe = await _mediaProbe
            .ProbeAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);

        if (!probe.VideoStreams.Any() && !probe.AudioStreams.Any())
        {
            throw new MediaProbeException(
                MediaProbeFailure.InvalidOutput,
                "The selected file contains no usable video or audio streams.");
        }

        var displayName = Path.GetFileName(probe.SourcePath);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = probe.SourcePath;
        }

        return new ImportedMedia(displayName, probe);
    }
}
