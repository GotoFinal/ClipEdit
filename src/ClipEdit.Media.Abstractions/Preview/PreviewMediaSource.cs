namespace ClipEdit.Media.Preview;

public sealed record PreviewMediaSource
{
    private PreviewMediaSource(
        string location,
        bool isRemote,
        string? remoteAudioLocation)
    {
        Location = location;
        IsRemote = isRemote;
        RemoteAudioLocation = remoteAudioLocation;
    }

    public string Location { get; }

    public bool IsRemote { get; }

    public string? RemoteAudioLocation { get; }

    public static PreviewMediaSource LocalFile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return new PreviewMediaSource(Path.GetFullPath(sourcePath), false, null);
    }

    public static PreviewMediaSource Internet(
        Uri videoUri,
        Uri? audioUri = null)
    {
        ValidateRemoteUri(videoUri, nameof(videoUri));
        if (audioUri is not null)
        {
            ValidateRemoteUri(audioUri, nameof(audioUri));
        }

        return new PreviewMediaSource(
            videoUri.AbsoluteUri,
            true,
            audioUri?.AbsoluteUri);
    }

    private static void ValidateRemoteUri(Uri uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "Remote preview requires an absolute HTTP or HTTPS URL without user information.",
                parameterName);
        }
    }
}
