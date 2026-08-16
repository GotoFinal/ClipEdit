using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.Media.FFmpeg.Probe;

internal static class FfprobeKeyframeJsonParser
{
    public static KeyframeIndex Parse(
        int videoStreamIndex,
        string json,
        MediaTime timestampOrigin,
        MediaTime? sourceDuration = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new MediaProbeException(
                MediaProbeFailure.InvalidOutput,
                "ffprobe returned empty keyframe metadata.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("frames", out var frames) ||
                frames.ValueKind != JsonValueKind.Array)
            {
                return new KeyframeIndex(videoStreamIndex, []);
            }

            var timestamps = ImmutableArray.CreateBuilder<MediaTime>();
            foreach (var frame in frames.EnumerateArray())
            {
                var value = GetTimestamp(frame, "best_effort_timestamp_time") ??
                            GetTimestamp(frame, "pkt_dts_time");
                if (value is not { } timestamp)
                {
                    continue;
                }

                var normalized = timestamp - timestampOrigin;
                if (normalized < MediaTime.Zero ||
                    (sourceDuration is { } duration && normalized > duration))
                {
                    continue;
                }

                timestamps.Add(normalized);
            }

            return new KeyframeIndex(videoStreamIndex, timestamps.ToImmutable());
        }
        catch (MediaProbeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or OverflowException or ArgumentException)
        {
            throw new MediaProbeException(
                MediaProbeFailure.InvalidOutput,
                "ffprobe returned invalid keyframe metadata.",
                exception);
        }
    }

    private static MediaTime? GetTimestamp(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var span = text.AsSpan().Trim();
        var negative = span.Length > 0 && span[0] == '-';
        if (negative || (span.Length > 0 && span[0] == '+'))
        {
            span = span[1..];
        }

        var separator = span.IndexOf('.');
        var whole = separator < 0 ? span : span[..separator];
        var fraction = separator < 0 ? ReadOnlySpan<char>.Empty : span[(separator + 1)..];
        while (fraction.Length > 9 && fraction[^1] == '0')
        {
            fraction = fraction[..^1];
        }

        if (fraction.Length > 9 ||
            !long.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var wholeValue) ||
            (fraction.Length > 0 &&
             !long.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new FormatException("The keyframe timestamp is not a supported decimal value.");
        }

        var denominator = 1;
        for (var index = 0; index < fraction.Length; index++)
        {
            denominator = checked(denominator * 10);
        }

        var fractionValue = fraction.Length == 0
            ? 0L
            : long.Parse(fraction, NumberStyles.None, CultureInfo.InvariantCulture);
        var numerator = checked((wholeValue * denominator) + fractionValue);
        return new MediaTime(negative ? checked(-numerator) : numerator, denominator);
    }
}
