using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClipEdit.App.InternetMedia;

internal interface IYtDlpToolProvider
{
    string? TryGetInstalledPath();

    Task<string> EnsureAvailableAsync(bool checkForUpdate, CancellationToken cancellationToken);
}

internal sealed record YtDlpToolState(string TagName, string AssetName, string RelativePath);

internal sealed class YtDlpToolManager : IYtDlpToolProvider, IDisposable
{
    private const long MaximumReleaseResponseBytes = 2 * 1024 * 1024;
    private const long MaximumChecksumResponseBytes = 128 * 1024;
    private const long MaximumExecutableBytes = 256L * 1024 * 1024;
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");
    private readonly string _cacheRoot;
    private readonly string _statePath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public YtDlpToolManager(string cacheRoot, HttpClient? httpClient = null)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _statePath = Path.Combine(_cacheRoot, "current.json");
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    public string? TryGetInstalledPath()
    {
        try
        {
            var info = new FileInfo(_statePath);
            if (!info.Exists || info.Length > 16 * 1024)
            {
                return null;
            }

            var state = JsonSerializer.Deserialize(
                File.ReadAllText(_statePath),
                global::ClipEdit.App.Settings.AppSettingsJsonContext.Default.YtDlpToolState);
            if (state is null || string.IsNullOrWhiteSpace(state.RelativePath))
            {
                return null;
            }

            var path = Path.GetFullPath(Path.Combine(_cacheRoot, state.RelativePath));
            return IsWithin(path, _cacheRoot) && File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<string> EnsureAvailableAsync(
        bool checkForUpdate,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installedPath = TryGetInstalledPath();
            if (installedPath is not null && !checkForUpdate)
            {
                return installedPath;
            }

            YtDlpRelease release;
            try
            {
                release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                installedPath is not null &&
                exception is HttpRequestException or IOException or InternetMediaException)
            {
                return installedPath;
            }

            var existingState = LoadState();
            if (installedPath is not null &&
                string.Equals(existingState?.TagName, release.TagName, StringComparison.Ordinal))
            {
                return installedPath;
            }

            var versionDirectory = Path.Combine(_cacheRoot, "versions", SafePathPart(release.TagName));
            Directory.CreateDirectory(versionDirectory);
            var destinationPath = Path.Combine(versionDirectory, release.AssetName);
            if (!File.Exists(destinationPath))
            {
                await DownloadVerifiedAsync(release, destinationPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            ValidateExecutableHeader(destinationPath);
            EnsureExecutable(destinationPath);

            var relativePath = Path.GetRelativePath(_cacheRoot, destinationPath);
            SaveState(new YtDlpToolState(release.TagName, release.AssetName, relativePath));
            return destinationPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<YtDlpRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(LatestReleaseUri, acceptJson: true);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, MaximumReleaseResponseBytes, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var tagName = RequiredString(root, "tag_name");
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                throw new InternetMediaException("The yt-dlp release has no downloadable assets.");
            }

            var assetName = GetPlatformAssetName();
            JsonElement? selectedAsset = null;
            JsonElement? sumsAsset = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = OptionalString(asset, "name");
                if (string.Equals(name, assetName, StringComparison.Ordinal))
                {
                    selectedAsset = asset;
                }
                else if (string.Equals(name, "SHA2-256SUMS", StringComparison.Ordinal))
                {
                    sumsAsset = asset;
                }
            }

            if (selectedAsset is null)
            {
                throw new InternetMediaException($"The latest yt-dlp release does not contain {assetName}.");
            }

            var downloadUri = TrustedGitHubUri(RequiredString(selectedAsset.Value, "browser_download_url"));
            var expectedHash = ParseDigest(OptionalString(selectedAsset.Value, "digest"));
            if (expectedHash is null)
            {
                if (sumsAsset is null)
                {
                    throw new InternetMediaException("The yt-dlp release does not provide a SHA-256 checksum.");
                }
                var sumsUri = TrustedGitHubUri(RequiredString(sumsAsset.Value, "browser_download_url"));
                expectedHash = await DownloadChecksumAsync(sumsUri, assetName, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new YtDlpRelease(tagName, assetName, downloadUri, expectedHash);
        }
        catch (InternetMediaException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InternetMediaException("GitHub returned invalid yt-dlp release metadata.", exception);
        }
    }

    private async Task<string> DownloadChecksumAsync(
        Uri uri,
        string assetName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri, acceptJson: false);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, MaximumChecksumResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && IsSha256(parts[0]) &&
                string.Equals(parts[^1].TrimStart('*'), assetName, StringComparison.Ordinal))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new InternetMediaException($"The yt-dlp checksum list does not contain {assetName}.");
    }

    private async Task DownloadVerifiedAsync(
        YtDlpRelease release,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        try
        {
            using var request = CreateRequest(release.DownloadUri, acceptJson: false);
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumExecutableBytes)
            {
                throw new InternetMediaException("The yt-dlp executable is larger than the safety limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
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
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total = checked(total + read);
                if (total > MaximumExecutableBytes)
                {
                    throw new InternetMediaException("The yt-dlp executable is larger than the safety limit.");
                }
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualHash, release.Sha256, StringComparison.Ordinal))
            {
                throw new InternetMediaException("The downloaded yt-dlp executable failed SHA-256 verification.");
            }

            await destination.DisposeAsync().ConfigureAwait(false);
            ValidateExecutableHeader(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private YtDlpToolState? LoadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize(
                    File.ReadAllText(_statePath),
                    global::ClipEdit.App.Settings.AppSettingsJsonContext.Default.YtDlpToolState)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void SaveState(YtDlpToolState state)
    {
        Directory.CreateDirectory(_cacheRoot);
        var temporaryPath = Path.Combine(_cacheRoot, $".current.{Guid.NewGuid():N}.saving");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    state,
                    global::ClipEdit.App.Settings.AppSettingsJsonContext.Default.YtDlpToolState));
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri, bool acceptJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ClipEdit-InternetImport/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            acceptJson ? "application/vnd.github+json" : "application/octet-stream"));
        if (acceptJson)
        {
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        }
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
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
                throw new InternetMediaException("The yt-dlp update service returned too much data.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetPlatformAssetName()
    {
        if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.X64)
        {
            return "yt-dlp.exe";
        }
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.X64)
        {
            return "yt-dlp_linux";
        }
        throw new InternetMediaException("Automatic yt-dlp installation supports Windows x64 and Linux x64.");
    }

    private static void ValidateExecutableHeader(string path)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length ||
            (OperatingSystem.IsWindows()
                ? header[0] != 'M' || header[1] != 'Z'
                : header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F'))
        {
            throw new InternetMediaException("The downloaded yt-dlp asset is not an executable for this platform.");
        }
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
    }

    private static Uri TrustedGitHubUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InternetMediaException("The yt-dlp release contains an untrusted download URL.");
        }
        return uri;
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) is { Length: > 0 } value
            ? value
            : throw new InternetMediaException($"The yt-dlp release is missing {name}.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        return digest is not null && digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               IsSha256(digest[prefix.Length..])
            ? digest[prefix.Length..].ToLowerInvariant()
            : null;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string SafePathPart(string value)
    {
        var result = new string(value
            .Where(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            .Take(96)
            .ToArray());
        return result.Length > 0 ? result : "release";
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathFullyQualified(relative);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record YtDlpRelease(string TagName, string AssetName, Uri DownloadUri, string Sha256);
}
