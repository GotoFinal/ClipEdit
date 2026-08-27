using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Tests.InternetMedia;

public sealed class YtDlpInternetMediaClientTests
{
    [Fact]
    public async Task Prepared_import_resolves_separate_direct_video_and_audio_urls()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"clipedit-preview-{Guid.NewGuid():N}");
        try
        {
            var runner = new RecordingRunner(new YtDlpProcessResult(
                0,
                "https://cdn.example.test/video.webm\nhttps://cdn.example.test/audio.webm\n",
                string.Empty));
            var client = new YtDlpInternetMediaClient(
                new StubToolProvider(),
                new InternetMediaCache(cacheRoot),
                () => null,
                4,
                runner);
            var request = CreateRequest();

            var prepared = await client.PrepareImportAsync(request, 720, CancellationToken.None);

            Assert.Equal("https://cdn.example.test/video.webm", prepared.PreviewVideoUri.AbsoluteUri);
            Assert.Equal("https://cdn.example.test/audio.webm", prepared.PreviewAudioUri?.AbsoluteUri);
            Assert.Null(prepared.CompletedLocalPath);
            Assert.Contains("--get-url", runner.Arguments);
            Assert.Contains("b[height<=720]/bv*[height<=720]+ba/b", runner.Arguments);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("__CLIPEDIT_PROGRESS__ 42.5%|1048576|2097152|12", 0.425, 1048576L, 2097152L, 12)]
    [InlineData("[download] __CLIPEDIT_PROGRESS__NA|NA|NA|3", null, null, null, 3)]
    public void Parses_machine_progress(
        string line,
        double? fraction,
        long? downloaded,
        long? total,
        double remainingSeconds)
    {
        Assert.True(YtDlpInternetMediaClient.TryParseProgress(line, out var progress));
        Assert.Equal(fraction, progress!.Fraction);
        Assert.Equal(downloaded, progress.DownloadedBytes);
        Assert.Equal(total, progress.TotalBytes);
        Assert.Equal(TimeSpan.FromSeconds(remainingSeconds), progress.Remaining);
    }

    [Fact]
    public void Ignores_unrelated_output()
    {
        Assert.False(YtDlpInternetMediaClient.TryParseProgress("[download] Destination: media.webm", out _));
    }

    private static InternetMediaDownloadRequest CreateRequest()
    {
        var info = new InternetMediaInfo(
            new Uri("https://example.test/watch/1"),
            "Example",
            "Generic",
            TimeSpan.FromMinutes(1),
            [new InternetMediaFormat("1", "mp4", "h264", "aac", 1280, 720, 30, 128, 1_000)]);
        return new InternetMediaDownloadRequest(
            info,
            new InternetVideoQualityChoice(720, "720p"),
            new InternetAudioQualityChoice(128, "128 kbps"));
    }

    private sealed class StubToolProvider : IYtDlpToolProvider
    {
        public string? TryGetInstalledPath() => "yt-dlp";

        public Task<string> EnsureAvailableAsync(bool checkForUpdate, CancellationToken cancellationToken)
        {
            _ = checkForUpdate;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Path.GetFullPath(
                OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp"));
        }
    }

    private sealed class RecordingRunner(YtDlpProcessResult result) : IYtDlpProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<YtDlpProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            bool captureStandardOutput,
            Action<string>? standardOutputLine,
            CancellationToken cancellationToken)
        {
            _ = executablePath;
            _ = standardOutputLine;
            Assert.True(captureStandardOutput);
            cancellationToken.ThrowIfCancellationRequested();
            Arguments = arguments.ToArray();
            return Task.FromResult(result);
        }
    }
}
