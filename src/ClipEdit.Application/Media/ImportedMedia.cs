using ClipEdit.Media.Probe;

namespace ClipEdit.Application.Media;

public sealed record ImportedMedia
{
    public ImportedMedia(string displayName, MediaProbeResult probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(probe);

        DisplayName = displayName;
        Probe = probe;
    }

    public string DisplayName { get; }

    public MediaProbeResult Probe { get; }

    public bool HasVideo => Probe.VideoStreams.Any();

    public bool HasAudio => Probe.AudioStreams.Any();

    public bool IsExternalAudio => !HasVideo && HasAudio;
}
