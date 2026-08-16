using System.Globalization;

namespace ClipEdit.Media.FFmpeg.Export;

internal sealed class FfmpegProgressParser
{
    public TimeSpan EncodedDuration { get; private set; }

    public double? FramesPerSecond { get; private set; }

    public double? ProcessingSpeed { get; private set; }

    public bool IsComplete { get; private set; }

    public bool IsReportBoundary { get; private set; }

    public bool Parse(string line)
    {
        IsReportBoundary = false;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        var key = line[..separator];
        var value = line[(separator + 1)..];
        if (key == "out_time_us" &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) &&
            microseconds >= 0)
        {
            EncodedDuration = TimeSpan.FromTicks(checked(microseconds * 10));
            return true;
        }

        if (key == "out_time" &&
            TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timestamp) &&
            timestamp >= TimeSpan.Zero)
        {
            EncodedDuration = timestamp;
            return true;
        }

        if (key == "fps" && TryParsePositiveDouble(value, out var framesPerSecond))
        {
            FramesPerSecond = framesPerSecond;
            return true;
        }

        var trimmedValue = value.Trim();
        if (key == "speed" &&
            trimmedValue.EndsWith('x') &&
            TryParsePositiveDouble(trimmedValue[..^1], out var processingSpeed))
        {
            ProcessingSpeed = processingSpeed;
            return true;
        }

        if (key == "progress" && value is "continue" or "end")
        {
            IsComplete = value == "end";
            IsReportBoundary = true;
            return true;
        }

        return false;
    }

    private static bool TryParsePositiveDouble(string value, out double result) =>
        double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result) &&
        result > 0 &&
        double.IsFinite(result);
}
