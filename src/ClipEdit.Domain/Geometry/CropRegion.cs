namespace ClipEdit.Domain.Geometry;

/// <summary>
/// A crop in integer pixels of a rotation-corrected source raster.
/// </summary>
public readonly record struct CropRegion
{
    private readonly int _widthMinusOne;
    private readonly int _heightMinusOne;

    public CropRegion(PixelSize sourceSize, int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if ((long)x + width > sourceSize.Width)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "The crop extends beyond the source width.");
        }

        if ((long)y + height > sourceSize.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                "The crop extends beyond the source height.");
        }

        SourceSize = sourceSize;
        X = x;
        Y = y;
        _widthMinusOne = width - 1;
        _heightMinusOne = height - 1;
    }

    public PixelSize SourceSize { get; }

    public int X { get; }

    public int Y { get; }

    public int Width => _widthMinusOne + 1;

    public int Height => _heightMinusOne + 1;

    public PixelSize ExportSize => new(Width, Height);

    public bool IsFullFrame =>
        X == 0 &&
        Y == 0 &&
        Width == SourceSize.Width &&
        Height == SourceSize.Height;

    public static CropRegion FullFrame(PixelSize sourceSize)
    {
        return new CropRegion(sourceSize, 0, 0, sourceSize.Width, sourceSize.Height);
    }
}
