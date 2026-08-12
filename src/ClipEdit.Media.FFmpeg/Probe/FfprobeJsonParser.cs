using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.Media.FFmpeg.Probe;

internal static class FfprobeJsonParser
{
    public static MediaProbeResult Parse(string sourcePath, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new MediaProbeException(
                MediaProbeFailure.InvalidOutput,
                "ffprobe returned empty JSON metadata.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });

            var root = document.RootElement;
            var format = root.GetProperty("format");
            var streams = ParseStreams(root);

            return new MediaProbeResult(
                sourcePath,
                GetRequiredString(format, "format_name"),
                GetOptionalString(format, "format_long_name"),
                ParseSeconds(GetOptionalString(format, "start_time")) ?? MediaTime.Zero,
                ParseSeconds(GetOptionalString(format, "duration")),
                ParseNullableLong(format, "size"),
                ParseNullableLong(format, "bit_rate"),
                streams);
        }
        catch (MediaProbeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            KeyNotFoundException or
            FormatException or
            OverflowException or
            ArgumentException)
        {
            throw new MediaProbeException(
                MediaProbeFailure.InvalidOutput,
                "ffprobe returned incomplete or invalid JSON metadata.",
                exception);
        }
    }

    private static ImmutableArray<MediaStreamInfo> ParseStreams(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out var streamsElement) ||
            streamsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var streams = ImmutableArray.CreateBuilder<MediaStreamInfo>();
        foreach (var stream in streamsElement.EnumerateArray())
        {
            streams.Add(ParseStream(stream));
        }

        return streams.ToImmutable();
    }

    private static MediaStreamInfo ParseStream(JsonElement stream)
    {
        var index = GetRequiredInt32(stream, "index");
        var kind = ParseKind(GetOptionalString(stream, "codec_type"));
        var codecName = GetOptionalString(stream, "codec_name") ?? "unknown";
        var codecLongName = GetOptionalString(stream, "codec_long_name");
        var profile = GetOptionalString(stream, "profile");
        var language = GetNestedOptionalString(stream, "tags", "language");
        var title = GetNestedOptionalString(stream, "tags", "title");
        var isDefault = GetNestedOptionalInt32(stream, "disposition", "default") == 1;
        var isForced = GetNestedOptionalInt32(stream, "disposition", "forced") == 1;
        var timeBase = ParsePositiveTimeBase(GetOptionalString(stream, "time_base"));
        var startTime = ParseStreamTime(stream, "start_pts", "start_time", timeBase);
        var duration = ParseStreamTime(stream, "duration_ts", "duration", timeBase);

        return kind switch
        {
            MediaStreamKind.Video => new VideoStreamInfo(
                index,
                codecName,
                codecLongName,
                profile,
                language,
                title,
                isDefault,
                isForced,
                timeBase,
                startTime,
                duration,
                new PixelSize(
                    GetRequiredInt32(stream, "width"),
                    GetRequiredInt32(stream, "height")),
                ParseRotation(stream),
                ParseFrameRate(GetOptionalString(stream, "r_frame_rate")),
                ParseFrameRate(GetOptionalString(stream, "avg_frame_rate")),
                GetOptionalString(stream, "pix_fmt"),
                GetOptionalString(stream, "sample_aspect_ratio"),
                GetOptionalString(stream, "display_aspect_ratio"),
                GetOptionalString(stream, "color_range"),
                GetOptionalString(stream, "color_space"),
                GetOptionalString(stream, "color_transfer"),
                GetOptionalString(stream, "color_primaries"),
                GetOptionalString(stream, "field_order")),
            MediaStreamKind.Audio => new AudioStreamInfo(
                index,
                codecName,
                codecLongName,
                profile,
                language,
                title,
                isDefault,
                isForced,
                timeBase,
                startTime,
                duration,
                ParseNullableInt32(stream, "sample_rate"),
                ParseNullableInt32(stream, "channels"),
                GetOptionalString(stream, "channel_layout"),
                GetOptionalString(stream, "sample_fmt")),
            _ => new OtherStreamInfo(
                index,
                kind,
                codecName,
                codecLongName,
                profile,
                language,
                title,
                isDefault,
                isForced,
                timeBase,
                startTime,
                duration),
        };
    }

    private static MediaTime? ParseStreamTime(
        JsonElement stream,
        string ticksProperty,
        string secondsProperty,
        MediaTime? timeBase)
    {
        var ticks = ParseNullableLong(stream, ticksProperty);
        if (ticks is not null && timeBase is not null)
        {
            return timeBase.Value * ticks.Value;
        }

        return ParseSeconds(GetOptionalString(stream, secondsProperty));
    }

    private static MediaTime? ParsePositiveTimeBase(string? value)
    {
        var parts = SplitRational(value);
        if (parts is null || parts.Value.Numerator <= 0 || parts.Value.Denominator <= 0)
        {
            return null;
        }

        return new MediaTime(parts.Value.Numerator, parts.Value.Denominator);
    }

    private static FrameRate? ParseFrameRate(string? value)
    {
        var parts = SplitRational(value);
        if (parts is null || parts.Value.Numerator <= 0 || parts.Value.Denominator <= 0)
        {
            return null;
        }

        return new FrameRate(parts.Value.Numerator, parts.Value.Denominator);
    }

    private static (long Numerator, int Denominator)? SplitRational(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return null;
        }

        if (!long.TryParse(
                value.AsSpan(0, separator),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var numerator) ||
            !int.TryParse(
                value.AsSpan(separator + 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var denominator))
        {
            return null;
        }

        return (numerator, denominator);
    }

    private static MediaTime? ParseSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        var isNegative = span.Length > 0 && span[0] == '-';
        if (isNegative || (span.Length > 0 && span[0] == '+'))
        {
            span = span[1..];
        }

        var decimalPoint = span.IndexOf('.');
        var wholeSpan = decimalPoint >= 0 ? span[..decimalPoint] : span;
        var fractionSpan = decimalPoint >= 0 ? span[(decimalPoint + 1)..] : ReadOnlySpan<char>.Empty;

        while (fractionSpan.Length > 9 && fractionSpan[^1] == '0')
        {
            fractionSpan = fractionSpan[..^1];
        }

        if (fractionSpan.Length > 9 ||
            !long.TryParse(wholeSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) ||
            (fractionSpan.Length > 0 &&
             !int.TryParse(fractionSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new FormatException("The ffprobe timestamp is not a supported decimal value.");
        }

        var denominator = PowerOfTen(fractionSpan.Length);
        var fraction = fractionSpan.Length == 0
            ? 0L
            : long.Parse(fractionSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        var numerator = checked((whole * denominator) + fraction);
        if (isNegative)
        {
            numerator = checked(-numerator);
        }

        return new MediaTime(numerator, denominator);
    }

    private static int PowerOfTen(int exponent)
    {
        var result = 1;
        for (var index = 0; index < exponent; index++)
        {
            result = checked(result * 10);
        }

        return result;
    }

    private static int ParseRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) &&
            sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                var rotation = ParseNullableInt32(item, "rotation");
                if (rotation is not null)
                {
                    return rotation.Value;
                }
            }
        }

        var tagRotation = GetNestedOptionalString(stream, "tags", "rotate");
        return int.TryParse(tagRotation, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static MediaStreamKind ParseKind(string? value) => value switch
    {
        "video" => MediaStreamKind.Video,
        "audio" => MediaStreamKind.Audio,
        "subtitle" => MediaStreamKind.Subtitle,
        "attachment" => MediaStreamKind.Attachment,
        "data" => MediaStreamKind.Data,
        _ => MediaStreamKind.Unknown,
    };

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return GetOptionalString(element, propertyName) ??
               throw new FormatException($"Required ffprobe field '{propertyName}' is missing.");
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static int GetRequiredInt32(JsonElement element, string propertyName)
    {
        return ParseNullableInt32(element, propertyName) ??
               throw new FormatException($"Required ffprobe field '{propertyName}' is missing.");
    }

    private static int? ParseNullableInt32(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseNullableLong(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? GetNestedOptionalInt32(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        return element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ParseNullableInt32(nested, propertyName)
            : null;
    }

    private static string? GetNestedOptionalString(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        return element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? GetOptionalString(nested, propertyName)
            : null;
    }
}
