using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Tests.InternetMedia;

public sealed class YtDlpToolManagerTests
{
    [Fact]
    public async Task Downloads_and_verifies_the_platform_executable_once()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipedit-ytdlp-tool-{Guid.NewGuid():N}");
        var executable = CreateExecutableBytes();
        var sha256 = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        var assetName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp_linux";
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "api.github.com")
            {
                var json = $$"""
                    {"tag_name":"2026.08.19","assets":[{"name":"{{assetName}}","browser_download_url":"https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/{{assetName}}","digest":"sha256:{{sha256}}"}]}
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(executable),
            };
        }));

        try
        {
            using var manager = new YtDlpToolManager(root, httpClient);
            var first = await manager.EnsureAvailableAsync(false, CancellationToken.None);
            var second = await manager.EnsureAvailableAsync(false, CancellationToken.None);

            Assert.Equal(first, second);
            Assert.True(File.Exists(first));
            Assert.Equal(2, requests);
            Assert.Equal(first, manager.TryGetInstalledPath());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Rejects_an_executable_with_the_wrong_checksum()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipedit-ytdlp-tool-{Guid.NewGuid():N}");
        var executable = CreateExecutableBytes();
        var assetName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp_linux";
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var json = $$"""
                    {"tag_name":"2026.08.19","assets":[{"name":"{{assetName}}","browser_download_url":"https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/{{assetName}}","digest":"sha256:{{new string('0', 64)}}"}]}
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(executable),
            };
        }));

        try
        {
            using var manager = new YtDlpToolManager(root, httpClient);
            var exception = await Assert.ThrowsAsync<InternetMediaException>(() =>
                manager.EnsureAvailableAsync(false, CancellationToken.None));

            Assert.Contains("SHA-256", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(manager.TryGetInstalledPath());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] CreateExecutableBytes() => OperatingSystem.IsWindows()
        ? [(byte)'M', (byte)'Z', 0, 0, 1, 2, 3, 4]
        : [0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3, 4];

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
