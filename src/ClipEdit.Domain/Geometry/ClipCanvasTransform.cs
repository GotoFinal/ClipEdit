namespace ClipEdit.Domain.Geometry;

/// <summary>
/// Places one source raster on a project canvas. The source rotates first;
/// horizontal and vertical scales then act in canvas axes, matching the live
/// preview controls. Offsets move the transformed source center relative to
/// the canvas center.
/// </summary>
public readonly record struct ClipCanvasTransform
{
    public ClipCanvasTransform(
        double offsetX,
        double offsetY,
        double scale,
        int rotationDegrees,
        bool isHorizontallyMirrored = false,
        bool isVerticallyMirrored = false)
        : this(
            offsetX,
            offsetY,
            scale,
            scale,
            rotationDegrees,
            isHorizontallyMirrored,
            isVerticallyMirrored)
    {
    }

    public ClipCanvasTransform(
        double offsetX,
        double offsetY,
        double scaleX,
        double scaleY,
        int rotationDegrees,
        bool isHorizontallyMirrored = false,
        bool isVerticallyMirrored = false)
    {
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) ||
            Math.Abs(offsetX) > 1_000_000_000 || Math.Abs(offsetY) > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetX), "Canvas offsets must be finite and bounded.");
        }

        if (!IsValidScale(scaleX) || !IsValidScale(scaleY))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scaleX),
                "Canvas scale on each axis must be between 0.01 and 100.");
        }

        OffsetX = offsetX;
        OffsetY = offsetY;
        ScaleX = scaleX;
        ScaleY = scaleY;
        RotationDegrees = ((rotationDegrees % 360) + 360) % 360;
        IsHorizontallyMirrored = isHorizontallyMirrored;
        IsVerticallyMirrored = isVerticallyMirrored;
    }

    public double OffsetX { get; }

    public double OffsetY { get; }

    public double ScaleX { get; }

    public double ScaleY { get; }

    public double Scale => ScaleX;

    public bool HasUniformScale => Math.Abs(ScaleX - ScaleY) < 0.000_001;

    public int RotationDegrees { get; }

    public bool IsHorizontallyMirrored { get; }

    public bool IsVerticallyMirrored { get; }

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
        new(
            offsetX,
            offsetY,
            ScaleX,
            ScaleY,
            RotationDegrees,
            IsHorizontallyMirrored,
            IsVerticallyMirrored);

    public ClipCanvasTransform Resize(double scale) =>
        new(
            OffsetX,
            OffsetY,
            scale,
            scale,
            RotationDegrees,
            IsHorizontallyMirrored,
            IsVerticallyMirrored);

    public ClipCanvasTransform Resize(double scaleX, double scaleY) =>
        new(
            OffsetX,
            OffsetY,
            scaleX,
            scaleY,
            RotationDegrees,
            IsHorizontallyMirrored,
            IsVerticallyMirrored);

    public ClipCanvasTransform Rotate(int rotationDegrees) =>
        new(
            OffsetX,
            OffsetY,
            ScaleX,
            ScaleY,
            rotationDegrees,
            IsHorizontallyMirrored,
            IsVerticallyMirrored);

    public ClipCanvasTransform MirrorHorizontally(bool isMirrored) =>
        new(
            OffsetX,
            OffsetY,
            ScaleX,
            ScaleY,
            RotationDegrees,
            isMirrored,
            IsVerticallyMirrored);

    public ClipCanvasTransform MirrorVertically(bool isMirrored) =>
        new(
            OffsetX,
            OffsetY,
            ScaleX,
            ScaleY,
            RotationDegrees,
            IsHorizontallyMirrored,
            isMirrored);

    public ClipCanvasTransform RotateCanvasClockwise() =>
        new(
            -OffsetY,
            OffsetX,
            ScaleY,
            ScaleX,
            RotationDegrees + 90,
            IsHorizontallyMirrored,
            IsVerticallyMirrored);

    private static bool IsValidScale(double scale) =>
        double.IsFinite(scale) && scale is >= 0.01 and <= 100;
}
