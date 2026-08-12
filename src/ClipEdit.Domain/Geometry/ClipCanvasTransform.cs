namespace ClipEdit.Domain.Geometry;

/// <summary>
/// Places one source raster on a project canvas. Scale is canvas pixels per
/// rotation-corrected source pixel; offsets move the transformed source center
/// relative to the canvas center.
/// </summary>
public readonly record struct ClipCanvasTransform
{
    public ClipCanvasTransform(
        double offsetX,
        double offsetY,
        double scale,
        int rotationDegrees)
    {
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) ||
            Math.Abs(offsetX) > 1_000_000_000 || Math.Abs(offsetY) > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetX), "Canvas offsets must be finite and bounded.");
        }

        if (!double.IsFinite(scale) || scale is < 0.01 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Canvas scale must be between 0.01 and 100.");
        }

        OffsetX = offsetX;
        OffsetY = offsetY;
        Scale = scale;
        RotationDegrees = ((rotationDegrees % 360) + 360) % 360;
    }

    public double OffsetX { get; }

    public double OffsetY { get; }

    public double Scale { get; }

    public int RotationDegrees { get; }

    public static ClipCanvasTransform Identity => new(0, 0, 1, 0);

    public static ClipCanvasTransform Fill(PixelSize sourceSize, PixelSize canvasSize) =>
        new(
            0,
            0,
            Math.Max(
                canvasSize.Width / (double)sourceSize.Width,
                canvasSize.Height / (double)sourceSize.Height),
            0);

    public static ClipCanvasTransform Fit(PixelSize sourceSize, PixelSize canvasSize) =>
        new(
            0,
            0,
            Math.Min(
                canvasSize.Width / (double)sourceSize.Width,
                canvasSize.Height / (double)sourceSize.Height),
            0);

    public ClipCanvasTransform MoveTo(double offsetX, double offsetY) =>
        new(offsetX, offsetY, Scale, RotationDegrees);

    public ClipCanvasTransform Resize(double scale) =>
        new(OffsetX, OffsetY, scale, RotationDegrees);

    public ClipCanvasTransform Rotate(int rotationDegrees) =>
        new(OffsetX, OffsetY, Scale, rotationDegrees);
}
