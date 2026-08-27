using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Tests.InternetMedia;

public sealed class YtDlpLiveSmokeTests
{
    [Fact]
    public async Task Official_release_downloads_verifies_and_runs()
    {
        if (Environment.GetEnvironmentVariable("CLIPEDIT_TEST_YTDLP_LIVE") != "1")
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"clipedit-ytdlp-live-{Guid.NewGuid():N}");
        try
        {
            using var manager = new YtDlpToolManager(root);
            var path = await manager.EnsureAvailableAsync(false, CancellationToken.None);
            var result = await new YtDlpProcessRunner().RunAsync(
                path,
                ["--version"],
                captureStandardOutput: true,
                standardOutputLine: null,
                CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
