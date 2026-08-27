using System.Globalization;

namespace ClipEdit.App.InternetMedia;

internal interface IInternetMediaClient
{
    Task<InternetMediaInfo> ProbeAsync(Uri uri, CancellationToken cancellationToken);

    Task<InternetMediaDownloadResult> DownloadAsync(
        InternetMediaDownloadRequest request,
        IProgress<InternetMediaDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

internal sealed class YtDlpInternetMediaClient : IInternetMediaClient
{
    private readonly IYtDlpToolProvider _toolProvider;
    private readonly IYtDlpProcessRunner _processRunner;
    private readonly InternetMediaCache _cache;
    private readonly Func<string?> _ffmpegPath;
    private readonly int _concurrentFragments;

    public YtDlpInternetMediaClient(
        IYtDlpToolProvider toolProvider,
        InternetMediaCache cache,
        Func<string?> ffmpegPath,
        int concurrentFragments,
        IYtDlpProcessRunner? processRunner = null)
    {
        _toolProvider = toolProvider ?? throw new ArgumentNullException(nameof(toolProvider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
        _concurrentFragments = Math.Clamp(concurrentFragments, 1, 16);
        _processRunner = processRunner ?? new YtDlpProcessRunner();
    }

    public async Task<InternetMediaInfo> ProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        var toolPath = await _toolProvider.EnsureAvailableAsync(
                checkForUpdate: false,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _processRunner.RunAsync(
                toolPath,
                YtDlpArguments.CreateProbe(uri),
                captureStandardOutput: true,
                standardOutputLine: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateProcessException("Could not inspect this link", result.StandardError);
        }
        return YtDlpJsonParser.Parse(uri, result.StandardOutput);
    }

    public async Task<InternetMediaDownloadResult> DownloadAsync(
        InternetMediaDownloadRequest request,
        IProgress<InternetMediaDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entryDirectory = _cache.GetEntryDirectory(request);
        if (_cache.TryGetCompletedPath(entryDirectory) is { } cachedPath)
        {
            progress?.Report(new InternetMediaDownloadProgress(1, null, null, TimeSpan.Zero));
            return new InternetMediaDownloadResult(cachedPath, request);
        }

        var toolPath = await _toolProvider.EnsureAvailableAsync(
                checkForUpdate: false,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _processRunner.RunAsync(
                toolPath,
                YtDlpArguments.CreateDownload(
                    request,
                    entryDirectory,
                    _ffmpegPath(),
                    _concurrentFragments),
                captureStandardOutput: false,
                line =>
                {
                    if (TryParseProgress(line, out var parsed))
                    {
                        progress?.Report(parsed!);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateProcessException("Could not download this media", result.StandardError);
        }

        var localPath = _cache.MarkCompleted(entryDirectory);
        progress?.Report(new InternetMediaDownloadProgress(1, null, null, TimeSpan.Zero));
        return new InternetMediaDownloadResult(localPath, request);
    }

    internal static bool TryParseProgress(string line, out InternetMediaDownloadProgress? progress)
    {
        progress = null;
        var prefixIndex = line.IndexOf(YtDlpArguments.ProgressPrefix, StringComparison.Ordinal);
        if (prefixIndex < 0)
        {
            return false;
        }
        var values = line[(prefixIndex + YtDlpArguments.ProgressPrefix.Length)..].Split('|');
        if (values.Length < 4)
        {
            return false;
        }

        var percentText = values[0].Trim().TrimEnd('%');
        double? fraction = double.TryParse(
            percentText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var percent) && double.IsFinite(percent)
            ? Math.Clamp(percent / 100, 0, 1)
            : null;
        var downloaded = ParseLong(values[1]);
        var total = ParseLong(values[2]);
        TimeSpan? remaining = double.TryParse(
            values[3].Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var etaSeconds) && etaSeconds >= 0 && etaSeconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(etaSeconds)
            : null;
        progress = new InternetMediaDownloadProgress(fraction, downloaded, total, remaining);
        return fraction is not null || downloaded is not null || total is not null || remaining is not null;
    }

    private static long? ParseLong(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;

    private static InternetMediaException CreateProcessException(string prefix, string diagnostic)
    {
        var message = diagnostic
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return new InternetMediaException(string.IsNullOrWhiteSpace(message) ? prefix : $"{prefix}: {message}");
    }
}
