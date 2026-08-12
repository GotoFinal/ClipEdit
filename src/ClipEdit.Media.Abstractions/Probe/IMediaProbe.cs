namespace ClipEdit.Media.Probe;

public interface IMediaProbe
{
    Task<MediaProbeResult> ProbeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
