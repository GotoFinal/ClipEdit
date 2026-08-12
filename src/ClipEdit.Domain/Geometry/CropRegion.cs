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

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public static CropRegion FullFrame(PixelSize sourceSize)
    {
        return new CropRegion(sourceSize, 0, 0, sourceSize.Width, sourceSize.Height);
    }

    public CropRegion MoveClamped(int x, int y)
    {
        var clampedX = Math.Clamp(x, 0, SourceSize.Width - Width);
        var clampedY = Math.Clamp(y, 0, SourceSize.Height - Height);
        return new CropRegion(SourceSize, clampedX, clampedY, Width, Height);
    }

    public static CropRegion FromEdges(
        PixelSize sourceSize,
        int left,
        int top,
        int right,
        int bottom)
    {
        if (right <= left)
        {
            throw new ArgumentOutOfRangeException(nameof(right), right, "The right edge must follow the left edge.");
        }

        if (bottom <= top)
        {
            throw new ArgumentOutOfRangeException(nameof(bottom), bottom, "The bottom edge must follow the top edge.");
        }

        return new CropRegion(sourceSize, left, top, right - left, bottom - top);
    }
}
