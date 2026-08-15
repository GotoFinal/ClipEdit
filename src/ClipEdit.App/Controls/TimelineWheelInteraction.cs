using Avalonia.Input;

namespace ClipEdit.App.Controls;

internal enum TimelineWheelAction
{
    ScrollParent,
    ZoomTime,
    PanTime,
    ScaleWaveform,
}

internal static class TimelineWheelInteraction
{
    public static TimelineWheelAction Resolve(KeyModifiers modifiers, bool isWaveform)
    {
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            return isWaveform
                ? TimelineWheelAction.ScaleWaveform
                : TimelineWheelAction.ScrollParent;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            return TimelineWheelAction.PanTime;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            return TimelineWheelAction.ZoomTime;
        }

        return isWaveform
            ? TimelineWheelAction.ScrollParent
            : TimelineWheelAction.ZoomTime;
    }
}
