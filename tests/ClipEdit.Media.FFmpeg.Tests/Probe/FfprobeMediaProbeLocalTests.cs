using ClipEdit.Media.FFmpeg.Probe;

namespace ClipEdit.Media.FFmpeg.Tests.Probe;

public sealed class FfprobeMediaProbeLocalTests
{
    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Probe_reads_the_opt_in_local_media_sample()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffprobePath = FfprobeExecutableLocator.Find();

        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffprobePath is null)
        {
            return;
        }

        var probe = new FfprobeMediaProbe(ffprobePath);

        var result = await probe.ProbeAsync(sourcePath, CancellationToken.None);

        Assert.NotEmpty(result.VideoStreams);
        Assert.NotEmpty(result.AudioStreams);
        Assert.NotNull(result.Duration);
    }
}
