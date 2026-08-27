using System.Globalization;

namespace ClipEdit.App.InternetMedia;

internal static class YtDlpArguments
{
    public const string ProgressPrefix = "__CLIPEDIT_PROGRESS__";

    public static IReadOnlyList<string> CreateProbe(Uri uri)
    {
        ValidateUri(uri);
        return
        [
            "--ignore-config",
            "--no-playlist",
            "--no-warnings",
            "--no-color",
            "--skip-download",
            "--dump-single-json",
            "--",
            uri.AbsoluteUri,
        ];
    }

    public static IReadOnlyList<string> CreateDownload(
        InternetMediaDownloadRequest request,
        string outputDirectory,
        string? ffmpegPath,
        int concurrentFragments)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUri(request.Info.SourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (concurrentFragments is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrentFragments));
        }

        var arguments = new List<string>
        {
            "--ignore-config",
            "--no-playlist",
            "--no-warnings",
            "--no-color",
            "--newline",
            "--continue",
            "--part",
            "--concurrent-fragments",
            concurrentFragments.ToString(CultureInfo.InvariantCulture),
            "--progress-template",
            $"download:{ProgressPrefix}%(progress._percent_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes_estimate)s|%(progress.eta)s",
            "--format",
            CreateFormatSelector(request.VideoQuality.MaximumHeight, request.AudioQuality.MaximumBitrateKbps),
            "--merge-output-format",
            "mkv",
            "--remux-video",
            "mkv",
            "--embed-chapters",
            "--paths",
            Path.GetFullPath(outputDirectory),
            "--output",
            "media.%(ext)s",
        };
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(Path.GetFullPath(ffmpegPath));
        }
        arguments.Add("--");
        arguments.Add(request.Info.SourceUri.AbsoluteUri);
        return arguments;
    }

    internal static string CreateFormatSelector(int? maximumHeight, int? maximumAudioBitrateKbps)
    {
        if (maximumHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        }
        if (maximumAudioBitrateKbps is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAudioBitrateKbps));
        }

        if (maximumHeight is null && maximumAudioBitrateKbps is null)
        {
            return "bv*+ba/b";
        }

        var video = maximumHeight is { } height ? $"bv*[height<={height}]" : "bv*";
        var combined = maximumHeight is { } combinedHeight ? $"b[height<={combinedHeight}]" : "b";
        if (maximumAudioBitrateKbps is not { } audioBitrate)
        {
            return $"{video}+ba/{combined}/b";
        }

        return $"{video}+ba[abr<={audioBitrate}]/{video}+ba/{combined}/b";
    }

    internal static string CreatePreviewFormatSelector(int maximumHeight)
    {
        if (maximumHeight is < 144 or > 2_160)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        }

        return $"b[height<={maximumHeight}]/bv*[height<={maximumHeight}]+ba/b";
    }

    public static IReadOnlyList<string> CreatePreviewResolve(Uri uri, int maximumHeight)
    {
        ValidateUri(uri);
        return
        [
            "--ignore-config",
            "--no-playlist",
            "--no-warnings",
            "--no-color",
            "--get-url",
            "--format",
            CreatePreviewFormatSelector(maximumHeight),
            "--",
            uri.AbsoluteUri,
        ];
    }

    private static void ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Only absolute HTTP or HTTPS media links are supported.", nameof(uri));
        }
    }
}
