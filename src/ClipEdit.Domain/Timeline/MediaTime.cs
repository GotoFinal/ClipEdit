using System.Globalization;

namespace ClipEdit.Domain.Timeline;

/// <summary>
/// An exact media timestamp expressed as a normalized rational number of seconds.
/// </summary>
public readonly record struct MediaTime : IComparable<MediaTime>
{
    private readonly int _denominatorMinusOne;

    public static MediaTime Zero { get; } = new(0, 1, isNormalized: true);

    public MediaTime(long numerator, int denominator)
    {
        var normalized = Normalize(numerator, denominator);
        Numerator = normalized.Numerator;
        _denominatorMinusOne = normalized.Denominator - 1;
    }

    private MediaTime(long numerator, int denominator, bool isNormalized)
    {
        _ = isNormalized;
        Numerator = numerator;
        _denominatorMinusOne = denominator - 1;
    }

    public long Numerator { get; }

    public int Denominator => _denominatorMinusOne + 1;

    public double TotalSeconds => (double)Numerator / Denominator;

    public int CompareTo(MediaTime other)
    {
        var left = (Int128)Numerator * other.Denominator;
        var right = (Int128)other.Numerator * Denominator;
        return left.CompareTo(right);
    }

    public override string ToString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Numerator}/{Denominator}s");
    }

    public static MediaTime operator +(MediaTime left, MediaTime right)
    {
        var numerator =
            ((Int128)left.Numerator * right.Denominator) +
            ((Int128)right.Numerator * left.Denominator);
        var denominator = (Int128)left.Denominator * right.Denominator;
        return Create(numerator, denominator);
    }

    public static MediaTime operator -(MediaTime left, MediaTime right)
    {
        var numerator =
            ((Int128)left.Numerator * right.Denominator) -
            ((Int128)right.Numerator * left.Denominator);
        var denominator = (Int128)left.Denominator * right.Denominator;
        return Create(numerator, denominator);
    }

    public static MediaTime operator -(MediaTime value)
    {
        return Create(-(Int128)value.Numerator, value.Denominator);
    }

    public static MediaTime operator *(MediaTime value, long multiplier)
    {
        return Create((Int128)value.Numerator * multiplier, value.Denominator);
    }

    public static MediaTime operator *(long multiplier, MediaTime value) => value * multiplier;

    public static MediaTime operator /(MediaTime value, long divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException();
        }

        return Create(value.Numerator, (Int128)value.Denominator * divisor);
    }

    public static bool operator <(MediaTime left, MediaTime right) => left.CompareTo(right) < 0;

    public static bool operator <=(MediaTime left, MediaTime right) => left.CompareTo(right) <= 0;

    public static bool operator >(MediaTime left, MediaTime right) => left.CompareTo(right) > 0;

    public static bool operator >=(MediaTime left, MediaTime right) => left.CompareTo(right) >= 0;

    private static MediaTime Create(Int128 numerator, Int128 denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException("A media time denominator cannot be zero.");
        }

        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var divisor = GreatestCommonDivisor(Abs(numerator), denominator);
        var normalizedNumerator = numerator / divisor;
        var normalizedDenominator = denominator / divisor;

        return new MediaTime(
            checked((long)normalizedNumerator),
            checked((int)normalizedDenominator),
            isNormalized: true);
    }

    private static (long Numerator, int Denominator) Normalize(long numerator, int denominator)
    {
        var normalized = Create(numerator, denominator);
        return (normalized.Numerator, normalized.Denominator);
    }

    private static Int128 GreatestCommonDivisor(Int128 left, Int128 right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    private static Int128 Abs(Int128 value) => value < 0 ? -value : value;
}
