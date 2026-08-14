using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClipEdit.App.Updates;

namespace ClipEdit.App.Tests.Updates;

public sealed class GitHubUpdateClientTests
{
    [Fact]
    public async Task Finds_newest_compatible_platform_release_even_when_newer_release_is_for_another_platform()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri?.Host);
            Assert.Contains("ClipEdit-UpdateChecker", request.Headers.UserAgent.ToString());
            Assert.True(request.Headers.Contains("X-GitHub-Api-Version"));
            return JsonResponse("""
                [
                  {
                    "tag_name":"v1.3.0","name":"Linux only","html_url":"https://github.com/GotoFinal/ClipEdit/releases/tag/v1.3.0",
                    "draft":false,"prerelease":false,"published_at":"2026-08-14T12:00:00Z",
                    "assets":[{"name":"ClipEdit-linux-x64","state":"uploaded","browser_download_url":"https://github.com/GotoFinal/ClipEdit/releases/download/v1.3.0/ClipEdit-linux-x64","size":123,"digest":null}]
                  },
                  {
                    "tag_name":"v1.2.0","name":"Windows","html_url":"https://github.com/GotoFinal/ClipEdit/releases/tag/v1.2.0",
                    "draft":false,"prerelease":false,"published_at":"2026-08-13T12:00:00Z",
                    "assets":[{"name":"ClipEdit-win-x64.exe","state":"uploaded","browser_download_url":"https://github.com/GotoFinal/ClipEdit/releases/download/v1.2.0/ClipEdit-win-x64.exe","size":456,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]
                  }
                ]
                """);
        });
        using var client = new GitHubUpdateClient(new HttpClient(handler));
        Assert.True(SemanticVersion.TryParse("1.0.0", out var current));

        var update = await client.CheckAsync(current, "win-x64", false, CancellationToken.None);

        Assert.NotNull(update);
        Assert.Equal("1.2.0", update.Version.ToString());
        Assert.Equal("ClipEdit-win-x64.exe", update.Asset.Name);
        Assert.False(update.IsPrerelease);
    }

    [Fact]
    public async Task Beta_releases_are_only_selected_when_the_preference_is_enabled()
    {
        const string releases = """
            [
              {
                "tag_name":"v2.0.0-beta.1","name":"Beta","html_url":"https://github.com/GotoFinal/ClipEdit/releases/tag/v2.0.0-beta.1",
                "draft":false,"prerelease":true,"published_at":"2026-08-14T12:00:00Z",
                "assets":[{"name":"ClipEdit-win-x64.exe","state":"uploaded","browser_download_url":"https://github.com/GotoFinal/ClipEdit/releases/download/v2.0.0-beta.1/ClipEdit-win-x64.exe","size":4,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]
              }
            ]
            """;
        using var handler = new StubHttpHandler(_ => JsonResponse(releases));
        using var client = new GitHubUpdateClient(new HttpClient(handler));
        Assert.True(SemanticVersion.TryParse("1.0.0", out var current));

        Assert.Null(await client.CheckAsync(current, "win-x64", false, CancellationToken.None));
        var beta = await client.CheckAsync(current, "win-x64", true, CancellationToken.None);

        Assert.NotNull(beta);
        Assert.True(beta.IsPrerelease);
        Assert.Equal("2.0.0-beta.1", beta.Version.ToString());
    }

    [Fact]
    public async Task Download_requires_and_verifies_sha256_before_staging_the_executable()
    {
        var executable = new byte[] { (byte)'M', (byte)'Z', 0, 0, 1, 2, 3, 4 };
        var digest = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        using var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
        });
        using var client = new GitHubUpdateClient(new HttpClient(handler));
        Assert.True(SemanticVersion.TryParse("1.1.0", out var version));
        var update = new AvailableUpdate(
            version,
            "v1.1.0",
            "ClipEdit 1.1.0",
            new Uri("https://github.com/GotoFinal/ClipEdit/releases/tag/v1.1.0"),
            DateTimeOffset.UtcNow,
            false,
            new UpdateAsset(
                "ClipEdit-win-x64.exe",
                new Uri("https://github.com/GotoFinal/ClipEdit/releases/download/v1.1.0/ClipEdit-win-x64.exe"),
                executable.Length,
                digest,
                null));
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"clipedit-updates-{Guid.NewGuid():N}");
        try
        {
            var staged = await client.DownloadAsync(update, stagingRoot, null, CancellationToken.None);

            Assert.Equal(executable, await File.ReadAllBytesAsync(staged.ExecutablePath));
            Assert.StartsWith(Path.GetFullPath(stagingRoot), staged.ExecutablePath);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Download_accepts_the_companion_checksum_emitted_by_the_release_build()
    {
        var executable = new byte[] { (byte)'M', (byte)'Z', 0, 0, 9, 8, 7, 6 };
        var digest = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        using var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{digest}  ClipEdit-win-x64.exe\n",
                        Encoding.UTF8,
                        "text/plain"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(executable),
            };
        });
        using var client = new GitHubUpdateClient(new HttpClient(handler));
        Assert.True(SemanticVersion.TryParse("1.1.1", out var version));
        var update = new AvailableUpdate(
            version,
            "v1.1.1",
            "ClipEdit 1.1.1",
            new Uri("https://github.com/GotoFinal/ClipEdit/releases/tag/v1.1.1"),
            DateTimeOffset.UtcNow,
            false,
            new UpdateAsset(
                "ClipEdit-win-x64.exe",
                new Uri("https://github.com/GotoFinal/ClipEdit/releases/download/v1.1.1/ClipEdit-win-x64.exe"),
                executable.Length,
                null,
                new Uri("https://github.com/GotoFinal/ClipEdit/releases/download/v1.1.1/ClipEdit-win-x64.exe.sha256")));
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"clipedit-updates-{Guid.NewGuid():N}");
        try
        {
            var staged = await client.DownloadAsync(update, stagingRoot, null, CancellationToken.None);
            Assert.Equal(executable, await File.ReadAllBytesAsync(staged.ExecutablePath));
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
