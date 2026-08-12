using System.Globalization;

namespace ClipEdit.Media.FFmpeg.Export;

internal sealed class FfmpegProgressParser
{
    public TimeSpan EncodedDuration { get; private set; }

    public bool IsComplete { get; private set; }

    public bool Parse(string line)
    {
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

        if (key == "progress" && value == "end")
        {
            IsComplete = true;
            return true;
        }

        return false;
    }
}
