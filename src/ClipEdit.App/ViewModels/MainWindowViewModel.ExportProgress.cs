using System.Globalization;
using ClipEdit.Media.Export;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal static string FormatExportProgress(
        ExportProgress progress,
        int progressPercent)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var parts = new List<string>
        {
            progress.Phase,
            $"{Math.Clamp(progressPercent, 0, 100)}%",
        };
        if (progress.FramesPerSecond is { } framesPerSecond)
        {
            parts.Add($"{framesPerSecond.ToString("0.0", CultureInfo.CurrentCulture)} FPS");
        }

        if (progress.EstimatedRemaining is { } remaining)
        {
            parts.Add($"{FormatRemainingTime(remaining)} remaining");
        }
        else if (progress.Fraction < 0.98 &&
                 progress.Phase is "Encoding" or "Copying")
        {
            parts.Add("estimating remaining time");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatRemainingTime(TimeSpan remaining)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds)));
        if (rounded >= TimeSpan.FromDays(1))
        {
            return $"{(int)rounded.TotalDays}d {rounded.Hours:00}:{rounded.Minutes:00}:{rounded.Seconds:00}";
        }

        return rounded >= TimeSpan.FromHours(1)
            ? $"{(int)rounded.TotalHours}:{rounded.Minutes:00}:{rounded.Seconds:00}"
            : $"{rounded.Minutes}:{rounded.Seconds:00}";
    }
}
