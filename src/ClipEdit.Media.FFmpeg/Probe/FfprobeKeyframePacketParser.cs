using System.Collections.Immutable;
using System.Globalization;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.Media.FFmpeg.Probe;

internal static class FfprobeKeyframePacketParser
{
    public static KeyframePoint? ParseLine(
        string line,
        MediaTime timestampOrigin,
        MediaTime? sourceDuration = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        MediaTime? presentationTimestamp = null;
        MediaTime? decodeTimestamp = null;
        var isKeyframe = false;
        foreach (var field in line.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = field[..separator];
            var value = field[(separator + 1)..];
            switch (name)
            {
                case "pts_time":
                    presentationTimestamp = ParseTimestamp(value);
                    break;
                case "dts_time":
                    decodeTimestamp = ParseTimestamp(value);
                    break;
                case "flags":
                    isKeyframe = value.Contains('K');
                    break;
            }
        }

        if (!isKeyframe || presentationTimestamp is not { } pts)
        {
            return null;
        }

        var normalized = pts - timestampOrigin;
        if (normalized < MediaTime.Zero ||
            (sourceDuration is { } duration && normalized > duration))
        {
            return null;
        }

        return new KeyframePoint(
            normalized,
            decodeTimestamp is { } dts ? dts - timestampOrigin : null);
    }

    public static KeyframeIndex CreateIndex(
        int videoStreamIndex,
        ImmutableArray<KeyframePoint> points) =>
        KeyframeIndex.FromPoints(videoStreamIndex, points);

    private static MediaTime? ParseTimestamp(string text)
    {
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
