namespace ClipEdit.App.InternetMedia;

internal static class InternetMediaUrl
{
    private const int MaximumUrlLength = 8_192;

    public static bool TryParse(string? text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidate = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => !line.StartsWith('#'));
        if (candidate is null || candidate.Length > MaximumUrlLength ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) ||
            parsed.UserInfo.Length > 0 ||
            parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
