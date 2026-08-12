using System.Collections.Immutable;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.Application.Tests.Media;

public sealed class ImportMediaUseCaseTests
{
    [Fact]
    public async Task Execute_classifies_a_video_with_embedded_audio()
    {
        var probeResult = CreateProbeResult(
            new VideoStreamInfo(
                0,
                "h264",
                null,
                null,
                null,
                null,
                true,
                false,
                new MediaTime(1, 1_000),
                MediaTime.Zero,
                new MediaTime(10, 1),
                new PixelSize(1_920, 1_080),
                0,
                new FrameRate(24, 1),
                new FrameRate(24, 1),
                "yuv420p",
                "1:1",
                "16:9",
                "tv",
                "bt709",
                "bt709",
                "bt709",
                "progressive"),
            CreateAudioStream());
        var useCase = new ImportMediaUseCase(new StubProbe(probeResult));

        var imported = await useCase.ExecuteAsync(probeResult.SourcePath);

        Assert.Equal("source.mkv", imported.DisplayName);
        Assert.True(imported.HasVideo);
        Assert.True(imported.HasAudio);
        Assert.False(imported.IsExternalAudio);
    }

    [Fact]
    public async Task Execute_classifies_an_audio_only_file_as_external_audio()
    {
        var probeResult = CreateProbeResult(CreateAudioStream());
        var useCase = new ImportMediaUseCase(new StubProbe(probeResult));

        var imported = await useCase.ExecuteAsync(probeResult.SourcePath);

        Assert.False(imported.HasVideo);
        Assert.True(imported.HasAudio);
        Assert.True(imported.IsExternalAudio);
    }

    [Fact]
    public async Task Execute_rejects_a_file_without_playable_streams()
    {
        var probeResult = CreateProbeResult(
            new OtherStreamInfo(
                0,
                MediaStreamKind.Attachment,
                "ttf",
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                null,
                null));
        var useCase = new ImportMediaUseCase(new StubProbe(probeResult));

        var exception = await Assert.ThrowsAsync<MediaProbeException>(
            () => useCase.ExecuteAsync(probeResult.SourcePath));

        Assert.Equal(MediaProbeFailure.InvalidOutput, exception.Failure);
    }

    private static AudioStreamInfo CreateAudioStream()
    {
        return new AudioStreamInfo(
            1,
            "aac",
            null,
            "LC",
            "jpn",
            null,
            true,
            false,
            new MediaTime(1, 1_000),
            MediaTime.Zero,
            new MediaTime(10, 1),
            44_100,
            2,
            "stereo",
            "fltp");
    }

    private static MediaProbeResult CreateProbeResult(params MediaStreamInfo[] streams)
    {
        var sourcePath = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "media", "source.mkv");
        return new MediaProbeResult(
            sourcePath,
            "matroska,webm",
            "Matroska / WebM",
            MediaTime.Zero,
            new MediaTime(10, 1),
            1_024,
            8_000,
            streams.ToImmutableArray());
    }

    private sealed class StubProbe(MediaProbeResult result) : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            _ = sourcePath;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
