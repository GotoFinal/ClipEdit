using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEdit.App.Updates;

internal sealed class GitHubUpdateClient : IUpdateClient
{
    private const long MaximumAssetBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumApiResponseBytes = 4 * 1024 * 1024;
    private const int MaximumChecksumBytes = 4 * 1024;
    private static readonly Uri ReleasesUri =
        new("https://api.github.com/repos/GotoFinal/ClipEdit/releases?per_page=100");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GitHubUpdateClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ownsHttpClient = httpClient is null;
    }

    public async Task<AvailableUpdate?> CheckAsync(
        SemanticVersion currentVersion,
        string runtimeId,
        bool includePrereleases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        var assetName = GetAssetName(runtimeId);
        using var request = CreateRequest(HttpMethod.Get, ReleasesUri, acceptJson: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("GitHub did not respond before the update check timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException($"Could not contact GitHub: {exception.Message}", exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new UpdateException(
                    "GitHub refused the update check, usually because its anonymous API limit was reached. Try again later.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(
                    $"GitHub update check failed with HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength is > MaximumApiResponseBytes)
            {
                throw new UpdateException("GitHub returned an unexpectedly large release list.");
            }

            var bytes = await ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false),
                MaximumApiResponseBytes,
                linked.Token).ConfigureAwait(false);
            GitHubReleaseDto[] releases;
            try
            {
                releases = JsonSerializer.Deserialize<GitHubReleaseDto[]>(bytes, JsonOptions) ?? [];
            }
            catch (JsonException exception)
            {
                throw new UpdateException("GitHub returned an invalid release list.", exception);
            }

            return SelectUpdate(releases, currentVersion, assetName, includePrereleases);
        }
    }

    public async Task<StagedUpdate> DownloadAsync(
        AvailableUpdate update,
        string stagingRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (!IsTrustedGitHubUri(update.Asset.DownloadUri) ||
            (update.Asset.ChecksumDownloadUri is { } checksumDownloadUri &&
             !IsTrustedGitHubUri(checksumDownloadUri)))
        {
            throw new UpdateException("The release asset URL is not hosted by GitHub.");
        }
        if (update.Asset.Size is <= 0 or > MaximumAssetBytes)
        {
            throw new UpdateException("The release asset has an invalid or unsafe size.");
        }

        var expectedSha256 = update.Asset.Sha256;
        if (expectedSha256 is null && update.Asset.ChecksumDownloadUri is { } checksumUri)
        {
            expectedSha256 = await DownloadChecksumAsync(
                checksumUri,
                update.Asset.Name,
                cancellationToken).ConfigureAwait(false);
        }
        if (expectedSha256 is null)
        {
            throw new UpdateException(
                "This release has no SHA-256 digest or checksum asset, so ClipEdit will not install it automatically.");
        }

        var fullStagingRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(fullStagingRoot);
        var stagingDirectory = Path.Combine(
            fullStagingRoot,
            $"{update.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var executableName = OperatingSystem.IsWindows() ? "ClipEdit-update.exe" : "ClipEdit-update";
        var temporaryPath = Path.Combine(stagingDirectory, $".{executableName}.downloading");
        var executablePath = Path.Combine(stagingDirectory, executableName);
        try
        {
            using var request = CreateRequest(HttpMethod.Get, update.Asset.DownloadUri, acceptJson: false);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(
                    $"GitHub asset download failed with HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != update.Asset.Size)
            {
                throw new UpdateException("The downloaded asset size does not match the GitHub release metadata.");
            }

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long totalBytes = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > update.Asset.Size || totalBytes > MaximumAssetBytes)
                {
                    throw new UpdateException("The downloaded update exceeded its declared size.");
                }
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(totalBytes / (double)update.Asset.Size);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
            if (totalBytes != update.Asset.Size)
            {
                throw new UpdateException("The downloaded update is incomplete.");
            }

            var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualSha256),
                    Convert.FromHexString(expectedSha256)))
            {
                throw new UpdateException("The downloaded update failed SHA-256 verification.");
            }

            ValidateExecutableHeader(temporaryPath, update.Asset.Name);
            File.Move(temporaryPath, executablePath);
            EnsureExecutable(executablePath);
            progress?.Report(1);
            return new StagedUpdate(update, executablePath, stagingDirectory);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static string GetAssetName(string runtimeId) => runtimeId switch
    {
        "win-x64" => "ClipEdit-win-x64.exe",
        "linux-x64" => "ClipEdit-linux-x64",
        _ => throw new UpdateException($"Automatic updates are unavailable for {runtimeId}."),
    };

    private static AvailableUpdate? SelectUpdate(
        IEnumerable<GitHubReleaseDto> releases,
        SemanticVersion currentVersion,
        string assetName,
        bool includePrereleases)
    {
        AvailableUpdate? selected = null;
        foreach (var release in releases)
        {
            if (release.Draft || (!includePrereleases && release.Prerelease) ||
                !SemanticVersion.TryParse(release.TagName, out var version) ||
                version <= currentVersion ||
                !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var releasePage) ||
                !IsTrustedGitHubUri(releasePage))
            {
                continue;
            }

            var asset = release.Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, assetName, StringComparison.Ordinal) &&
                string.Equals(candidate.State, "uploaded", StringComparison.OrdinalIgnoreCase));
            if (asset is null ||
                asset.Size is <= 0 or > MaximumAssetBytes ||
                !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri) ||
                !IsTrustedGitHubUri(downloadUri))
            {
                continue;
            }

            var checksumAssetName = assetName + ".sha256";
            var checksumAsset = release.Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, checksumAssetName, StringComparison.Ordinal) &&
                string.Equals(candidate.State, "uploaded", StringComparison.OrdinalIgnoreCase));
            Uri? checksumUri = null;
            if (checksumAsset is not null &&
                Uri.TryCreate(checksumAsset.BrowserDownloadUrl, UriKind.Absolute, out var parsedChecksumUri) &&
                IsTrustedGitHubUri(parsedChecksumUri))
            {
                checksumUri = parsedChecksumUri;
            }

            var digest = ParseDigest(asset.Digest);
            var publishedAt = release.PublishedAt ?? DateTimeOffset.MinValue;
            var candidate = new AvailableUpdate(
                version!,
                release.TagName ?? version!.ToString(),
                string.IsNullOrWhiteSpace(release.Name) ? $"ClipEdit {version}" : release.Name,
                releasePage,
                publishedAt,
                release.Prerelease,
                new UpdateAsset(assetName, downloadUri, asset.Size, digest, checksumUri));
            if (selected is null || candidate.Version > selected.Version)
            {
                selected = candidate;
            }
        }

        return selected;
    }

    private async Task<string> DownloadChecksumAsync(
        Uri uri,
        string assetName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri, acceptJson: false);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException("Could not download the release checksum.");
        }

        var bytes = await ReadBoundedAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            MaximumChecksumBytes,
            cancellationToken).ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && IsSha256(parts[0]) &&
                (parts.Length == 1 || string.Equals(parts[^1].TrimStart('*'), assetName, StringComparison.Ordinal)))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new UpdateException("The release checksum file is invalid.");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, bool acceptJson)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("ClipEdit-UpdateChecker/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            acceptJson ? "application/vnd.github+json" : "application/octet-stream"));
        if (acceptJson)
        {
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        }
        return request;
    }

    private static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        return digest is not null &&
               digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               IsSha256(digest[prefix.Length..])
            ? digest[prefix.Length..].ToLowerInvariant()
            : null;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsTrustedGitHubUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > maximumBytes)
            {
                throw new UpdateException("The update service returned more data than expected.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateExecutableHeader(string path, string assetName)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length ||
            (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? header[0] != 'M' || header[1] != 'Z'
                : header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F'))
        {
            throw new UpdateException("The downloaded asset is not a valid executable for this platform.");
        }
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAssetDto[] Assets { get; init; } = [];
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
