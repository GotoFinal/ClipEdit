using System.Diagnostics.CodeAnalysis;

namespace ClipEdit.App.Updates;

internal sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly string[] _preReleaseIdentifiers;

    private SemanticVersion(int major, int minor, int patch, string[] preReleaseIdentifiers)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preReleaseIdentifiers = preReleaseIdentifiers;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public bool IsPreRelease => _preReleaseIdentifiers.Length > 0;

    public static bool TryParse(
        string? text,
        [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidate = text.Trim();
        if (candidate.StartsWith('v') || candidate.StartsWith('V'))
        {
            candidate = candidate[1..];
        }

        var buildSeparator = candidate.IndexOf('+');
        if (buildSeparator >= 0)
        {
            var buildIdentifiers = candidate[(buildSeparator + 1)..].Split('.');
            if (!AreValidIdentifiers(buildIdentifiers, rejectNumericLeadingZeros: false))
            {
                return false;
            }
            candidate = candidate[..buildSeparator];
        }

        var preReleaseSeparator = candidate.IndexOf('-');
        var core = preReleaseSeparator >= 0 ? candidate[..preReleaseSeparator] : candidate;
        var preRelease = preReleaseSeparator >= 0 ? candidate[(preReleaseSeparator + 1)..] : null;
        var coreParts = core.Split('.');
        if (coreParts.Length != 3 ||
            !TryParseCoreNumber(coreParts[0], out var major) ||
            !TryParseCoreNumber(coreParts[1], out var minor) ||
            !TryParseCoreNumber(coreParts[2], out var patch))
        {
            return false;
        }

        var identifiers = preRelease is null
            ? []
            : preRelease.Split('.');
        if (!AreValidIdentifiers(identifiers, rejectNumericLeadingZeros: true))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, identifiers);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core == 0)
        {
            core = Minor.CompareTo(other.Minor);
        }
        if (core == 0)
        {
            core = Patch.CompareTo(other.Patch);
        }
        if (core != 0)
        {
            return core;
        }

        if (!IsPreRelease || !other.IsPreRelease)
        {
            return IsPreRelease == other.IsPreRelease ? 0 : IsPreRelease ? -1 : 1;
        }

        var sharedLength = Math.Min(_preReleaseIdentifiers.Length, other._preReleaseIdentifiers.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            var identifier = CompareIdentifier(
                _preReleaseIdentifiers[index],
                other._preReleaseIdentifiers[index]);
            if (identifier != 0)
            {
                return identifier;
            }
        }

        return _preReleaseIdentifiers.Length.CompareTo(other._preReleaseIdentifiers.Length);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in _preReleaseIdentifiers)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return IsPreRelease ? $"{core}-{string.Join('.', _preReleaseIdentifiers)}" : core;
    }

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    private static bool TryParseCoreNumber(string text, out int number)
    {
        number = 0;
        return text.Length > 0 &&
               (text.Length == 1 || text[0] != '0') &&
               int.TryParse(text, System.Globalization.NumberStyles.None, null, out number);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var length = left.Length.CompareTo(right.Length);
            return length != 0 ? length : string.Compare(left, right, StringComparison.Ordinal);
        }
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static bool AreValidIdentifiers(
        IEnumerable<string> identifiers,
        bool rejectNumericLeadingZeros) =>
        identifiers.All(identifier =>
            identifier.Length > 0 &&
            identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
            (!rejectNumericLeadingZeros ||
             !identifier.All(char.IsAsciiDigit) ||
             identifier.Length == 1 ||
             identifier[0] != '0'));
}
