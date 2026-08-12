namespace ClipEdit.Domain.Timeline;

/// <summary>
/// An exact non-negative number of frames per second.
/// </summary>
public readonly record struct FrameRate
{
    private readonly int _denominatorMinusOne;

    public FrameRate(long numerator, int denominator)
    {
        if (numerator < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numerator),
                numerator,
                "A frame rate cannot be negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);

        var divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        _denominatorMinusOne = checked((int)(denominator / divisor)) - 1;
    }

    public long Numerator { get; }

    public int Denominator => _denominatorMinusOne + 1;

    public double FramesPerSecond => (double)Numerator / Denominator;

    public bool IsZero => Numerator == 0;

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}
