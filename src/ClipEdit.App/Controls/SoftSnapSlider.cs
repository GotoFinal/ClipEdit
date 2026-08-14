using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ClipEdit.App.Controls;

public sealed class SoftSnapSlider : Slider
{
    protected override Type StyleKeyOverride => typeof(Slider);

    public static readonly StyledProperty<IReadOnlyList<double>> SnapValuesProperty =
        AvaloniaProperty.Register<SoftSnapSlider, IReadOnlyList<double>>(
            nameof(SnapValues),
            defaultValue: Array.Empty<double>());

    public static readonly StyledProperty<double> SnapThresholdProperty =
        AvaloniaProperty.Register<SoftSnapSlider, double>(
            nameof(SnapThreshold),
            defaultValue: 1.25);

    public IReadOnlyList<double> SnapValues
    {
        get => GetValue(SnapValuesProperty);
        set => SetValue(SnapValuesProperty, value);
    }

    public double SnapThreshold
    {
        get => GetValue(SnapThresholdProperty);
        set => SetValue(SnapThresholdProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        ApplySoftSnap(eventArgs.KeyModifiers);
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ApplySoftSnap(eventArgs.KeyModifiers);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        ApplySoftSnap(eventArgs.KeyModifiers);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        ApplySoftSnap(eventArgs.KeyModifiers);
    }

    private void ApplySoftSnap(KeyModifiers modifiers)
    {
        var snapped = CalculateSoftSnap(
            Value,
            SnapValues,
            SnapThreshold,
            modifiers.HasFlag(KeyModifiers.Shift));
        if (snapped != Value)
        {
            SetCurrentValue(ValueProperty, snapped);
        }
    }

    internal static double CalculateSoftSnap(
        double value,
        IReadOnlyList<double> snapValues,
        double threshold,
        bool bypass)
    {
        ArgumentNullException.ThrowIfNull(snapValues);
        if (bypass || !double.IsFinite(value) || !double.IsFinite(threshold) || threshold <= 0)
        {
            return value;
        }

        var nearest = value;
        var nearestDistance = threshold;
        foreach (var candidate in snapValues)
        {
            if (!double.IsFinite(candidate))
            {
                continue;
            }

            var distance = Math.Abs(candidate - value);
            if (distance <= nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        return nearest;
    }
}
