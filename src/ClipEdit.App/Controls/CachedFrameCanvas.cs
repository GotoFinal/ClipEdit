using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClipEdit.Domain.Geometry;
using DomainPixelSize = ClipEdit.Domain.Geometry.PixelSize;

namespace ClipEdit.App.Controls;

/// <summary>
/// Displays a cached source frame with the same canvas transform as the live
/// libmpv surface. It temporarily covers a slow exact seek without exposing an
/// unrelated keyframe from the decoder.
/// </summary>
public sealed class CachedFrameCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> ImageProperty =
        AvaloniaProperty.Register<CachedFrameCanvas, Bitmap?>(nameof(Image));

    public static readonly StyledProperty<DomainPixelSize> SourceSizeProperty =
        AvaloniaProperty.Register<CachedFrameCanvas, DomainPixelSize>(
            nameof(SourceSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<DomainPixelSize> CanvasSizeProperty =
        AvaloniaProperty.Register<CachedFrameCanvas, DomainPixelSize>(
            nameof(CanvasSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<ClipCanvasTransform> CanvasTransformProperty =
        AvaloniaProperty.Register<CachedFrameCanvas, ClipCanvasTransform>(
            nameof(CanvasTransform),
            ClipCanvasTransform.Identity);

    static CachedFrameCanvas()
    {
        AffectsRender<CachedFrameCanvas>(
            ImageProperty,
            SourceSizeProperty,
            CanvasSizeProperty,
            CanvasTransformProperty);
    }

    public Bitmap? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public DomainPixelSize SourceSize
    {
        get => GetValue(SourceSizeProperty);
        set => SetValue(SourceSizeProperty, value);
    }

    public DomainPixelSize CanvasSize
    {
        get => GetValue(CanvasSizeProperty);
        set => SetValue(CanvasSizeProperty, value);
    }

    public ClipCanvasTransform CanvasTransform
    {
        get => GetValue(CanvasTransformProperty);
        set => SetValue(CanvasTransformProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var image = Image;
        if (image is null)
        {
            return;
        }

        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        using (context.PushTransform(CalculateCanvasToViewportMatrix(
                   CanvasSize,
                   CanvasTransform,
                   Bounds.Size)))
        {
            context.DrawImage(
                image,
                new Rect(image.Size),
                new Rect(
                    -SourceSize.Width / 2d,
                    -SourceSize.Height / 2d,
                    SourceSize.Width,
                    SourceSize.Height));
        }
    }

    internal static Matrix CalculateCanvasToViewportMatrix(
        DomainPixelSize canvasSize,
        ClipCanvasTransform transform,
        Size viewportSize)
    {
        var viewportWidth = Math.Max(1, viewportSize.Width);
        var viewportHeight = Math.Max(1, viewportSize.Height);
        var displayScale = Math.Min(
            viewportWidth / canvasSize.Width,
            viewportHeight / canvasSize.Height);
        var radians = transform.RotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var matrix = new Matrix(
            cosine * transform.ScaleX * displayScale,
            sine * transform.ScaleY * displayScale,
            -sine * transform.ScaleX * displayScale,
            cosine * transform.ScaleY * displayScale,
            (viewportWidth / 2) + (transform.OffsetX * displayScale),
            (viewportHeight / 2) + (transform.OffsetY * displayScale));

        return new Matrix(
            transform.IsHorizontallyMirrored ? -matrix.M11 : matrix.M11,
            transform.IsHorizontallyMirrored ? -matrix.M12 : matrix.M12,
            transform.IsVerticallyMirrored ? -matrix.M21 : matrix.M21,
            transform.IsVerticallyMirrored ? -matrix.M22 : matrix.M22,
            matrix.M31,
            matrix.M32);
    }
}
