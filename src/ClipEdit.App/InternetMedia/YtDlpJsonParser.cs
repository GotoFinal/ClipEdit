using System.Collections.Immutable;
using System.Text.Json;

namespace ClipEdit.App.InternetMedia;

internal static class YtDlpJsonParser
{
    public static InternetMediaInfo Parse(Uri requestedUri, string json)
    {
        ArgumentNullException.ThrowIfNull(requestedUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 64,
            });
            var root = document.RootElement;
            var title = GetString(root, "title") ?? GetString(root, "id") ?? requestedUri.Host;
            var resolvedUri = Uri.TryCreate(GetString(root, "webpage_url"), UriKind.Absolute, out var parsedUri) &&
                              parsedUri.Scheme is "http" or "https"
                ? parsedUri
                : requestedUri;
            var durationSeconds = GetDouble(root, "duration");
            TimeSpan? duration = durationSeconds is > 0 && durationSeconds < TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(durationSeconds.Value)
                : null;
            var formats = ImmutableArray.CreateBuilder<InternetMediaFormat>();
            if (root.TryGetProperty("formats", out var formatArray) &&
                formatArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var format in formatArray.EnumerateArray())
                {
                    if (format.ValueKind != JsonValueKind.Object ||
                        GetString(format, "format_id") is not { Length: > 0 } id)
                    {
                        continue;
                    }

                    formats.Add(new InternetMediaFormat(
                        id,
                        GetString(format, "ext"),
                        GetString(format, "vcodec"),
                        GetString(format, "acodec"),
                        GetPositiveInt(format, "width"),
                        GetPositiveInt(format, "height"),
                        GetPositiveDouble(format, "fps"),
                        GetPositiveDouble(format, "abr"),
                        GetPositiveInt64(format, "filesize") ??
                        GetPositiveInt64(format, "filesize_approx")));
                }
            }

            if (formats.Count == 0 && GetString(root, "format_id") is { Length: > 0 } rootFormatId)
            {
                formats.Add(new InternetMediaFormat(
                    rootFormatId,
                    GetString(root, "ext"),
                    GetString(root, "vcodec"),
                    GetString(root, "acodec"),
                    GetPositiveInt(root, "width"),
                    GetPositiveInt(root, "height"),
                    GetPositiveDouble(root, "fps"),
                    GetPositiveDouble(root, "abr"),
                    GetPositiveInt64(root, "filesize") ?? GetPositiveInt64(root, "filesize_approx")));
            }

            if (formats.Count == 0)
            {
                throw new InternetMediaException("The link did not expose any downloadable media formats.");
            }

            return new InternetMediaInfo(
                resolvedUri,
                title.Trim(),
                GetString(root, "extractor_key") ?? GetString(root, "extractor"),
                duration,
                formats.ToImmutable());
        }
        catch (InternetMediaException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InternetMediaException("yt-dlp returned invalid media information.", exception);
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetPositiveInt(JsonElement element, string name)
    {
        var value = GetPositiveDouble(element, name);
        return value is <= int.MaxValue ? (int)Math.Round(value.Value) : null;
    }

    private static long? GetPositiveInt64(JsonElement element, string name)
    {
        var value = GetDouble(element, name);
        return value is > 0 and <= long.MaxValue ? (long)Math.Round(value.Value) : null;
    }

    private static double? GetPositiveDouble(JsonElement element, string name)
    {
        var value = GetDouble(element, name);
        return value is > 0 ? value : null;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) && double.IsFinite(number) => number,
            _ => null,
        };
    }
}
