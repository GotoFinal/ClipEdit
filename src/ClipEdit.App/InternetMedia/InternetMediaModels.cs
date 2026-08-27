using System.Collections.Immutable;

namespace ClipEdit.App.InternetMedia;

internal sealed record InternetMediaInfo(
    Uri SourceUri,
    string Title,
    string? Extractor,
    TimeSpan? Duration,
    ImmutableArray<InternetMediaFormat> Formats,
    ImmutableArray<InternetMediaChapter> Chapters = default)
{
    public IReadOnlyList<InternetVideoQualityChoice> CreateVideoQualityChoices()
    {
        var heights = Formats
            .Where(static format => format.HasVideo && format.Height is > 0)
            .Select(static format => format.Height!.Value)
            .Distinct()
            .OrderDescending()
            .ToArray();
        if (heights.Length <= 1)
        {
            return [new InternetVideoQualityChoice(null, heights.FirstOrDefault() is > 0 ? $"Best ({heights[0]}p)" : "Best")];
        }

        return
        [
            new InternetVideoQualityChoice(null, $"Best ({heights[0]}p)"),
            .. heights.Select(static height => new InternetVideoQualityChoice(height, $"Up to {height}p")),
        ];
    }

    public IReadOnlyList<InternetAudioQualityChoice> CreateAudioQualityChoices()
    {
        var bitRates = Formats
            .Where(static format => format.HasAudio && format.AudioBitrateKbps is > 0)
            .Select(static format => (int)Math.Round(
                format.AudioBitrateKbps!.Value,
                MidpointRounding.AwayFromZero))
            .Where(static bitRate => bitRate > 0)
            .Distinct()
            .OrderDescending()
            .ToArray();
        if (bitRates.Length <= 1)
        {
            return [new InternetAudioQualityChoice(null, bitRates.FirstOrDefault() is > 0 ? $"Best ({bitRates[0]} kbps)" : "Best")];
        }

        return
        [
            new InternetAudioQualityChoice(null, $"Best ({bitRates[0]} kbps)"),
            .. bitRates.Select(static bitRate =>
                new InternetAudioQualityChoice(bitRate, $"Up to {bitRate} kbps")),
        ];
    }

    public bool HasVideoQualityChoice =>
        Formats.Where(static format => format.HasVideo && format.Height is > 0)
            .Select(static format => format.Height)
            .Distinct()
            .Skip(1)
            .Any();

    public bool HasAudioQualityChoice =>
        Formats.Where(static format => format.HasAudio && format.AudioBitrateKbps is > 0)
            .Select(static format => (int)Math.Round(format.AudioBitrateKbps!.Value))
            .Distinct()
            .Skip(1)
            .Any();
}

internal sealed record InternetMediaFormat(
    string Id,
    string? Extension,
    string? VideoCodec,
    string? AudioCodec,
    int? Width,
    int? Height,
    double? FrameRate,
    double? AudioBitrateKbps,
    long? FileSizeBytes)
{
    public bool HasVideo => !string.IsNullOrWhiteSpace(VideoCodec) &&
                            !string.Equals(VideoCodec, "none", StringComparison.OrdinalIgnoreCase);

    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec) &&
                            !string.Equals(AudioCodec, "none", StringComparison.OrdinalIgnoreCase);
}

internal sealed record InternetMediaChapter(string Title, TimeSpan Start, TimeSpan End);

internal sealed record InternetVideoQualityChoice(int? MaximumHeight, string DisplayName);

internal sealed record InternetAudioQualityChoice(int? MaximumBitrateKbps, string DisplayName);

internal sealed record InternetMediaDownloadRequest(
    InternetMediaInfo Info,
    InternetVideoQualityChoice VideoQuality,
    InternetAudioQualityChoice AudioQuality);

internal sealed record InternetMediaDownloadProgress(
    double? Fraction,
    long? DownloadedBytes,
    long? TotalBytes,
    TimeSpan? Remaining);

internal sealed record InternetMediaDownloadResult(string LocalPath, InternetMediaDownloadRequest Request);

internal sealed record InternetMediaPreparedImport(
    InternetMediaDownloadRequest Request,
    string ExpectedLocalPath,
    Uri PreviewVideoUri,
    Uri? PreviewAudioUri,
    int PreviewMaximumHeight,
    string? CompletedLocalPath);

internal sealed class InternetMediaException : Exception
{
    public InternetMediaException(string message)
        : base(message)
    {
    }

    public InternetMediaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
