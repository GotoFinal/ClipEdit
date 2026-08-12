namespace ClipEdit.Domain.Geometry;

public readonly record struct PixelSize
{
    private readonly int _widthMinusOne;
    private readonly int _heightMinusOne;

    public PixelSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _widthMinusOne = width - 1;
        _heightMinusOne = height - 1;
    }

    public int Width => _widthMinusOne + 1;

    public int Height => _heightMinusOne + 1;
}
