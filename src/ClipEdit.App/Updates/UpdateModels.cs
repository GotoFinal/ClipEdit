namespace ClipEdit.App.Updates;

internal sealed record UpdateAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Sha256,
    Uri? ChecksumDownloadUri);

internal sealed record AvailableUpdate(
    SemanticVersion Version,
    string TagName,
    string DisplayName,
    Uri ReleasePageUri,
    DateTimeOffset PublishedAt,
    bool IsPrerelease,
    UpdateAsset Asset);

internal sealed record StagedUpdate(
    AvailableUpdate Release,
    string ExecutablePath,
    string StagingDirectory);

internal sealed class UpdateException : Exception
{
    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal interface IUpdateClient : IDisposable
{
    Task<AvailableUpdate?> CheckAsync(
        SemanticVersion currentVersion,
        string releaseAssetId,
        bool includePrereleases,
        CancellationToken cancellationToken);

    Task<StagedUpdate> DownloadAsync(
        AvailableUpdate update,
        string stagingRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
