using System.Globalization;
using System.Text;
using ClipEdit.Media.Export;

namespace ClipEdit.Media.FFmpeg.Export;

internal static class FfconcatManifest
{
    public static string Create(ExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Strategy != ExportStrategy.ConcatStreamCopy || !plan.IsSequence)
        {
            throw new ArgumentException(
                "An ffconcat manifest requires a packet-copy concatenation plan.",
                nameof(plan));
        }

        var manifest = new StringBuilder("ffconcat version 1.0\n");
        foreach (var segment in plan.VideoSegments)
        {
            manifest.Append("file ")
                .Append(EscapePath(segment.SourcePath))
                .Append('\n');
            manifest.Append("duration ")
                .Append(FormatTime(segment.SourceRange.Duration))
                .Append('\n');
        }

        return manifest.ToString();
    }

    public static string CreatePaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var materialized = paths.ToArray();
        if (materialized.Length == 0 || materialized.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one media path is required.", nameof(paths));
        }

        var manifest = new StringBuilder("ffconcat version 1.0\n");
        foreach (var path in materialized)
        {
            manifest.Append("file ")
                .Append(EscapePath(Path.GetFullPath(path)))
                .Append('\n');
        }

        return manifest.ToString();
    }

    private static string EscapePath(string path)
    {
        if (path.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ExportPlanException("A media path cannot be represented in an ffconcat manifest.");
        }

        var ffmpegPath = OperatingSystem.IsWindows()
            ? path.Replace('\\', '/')
            : path;
        return $"'{ffmpegPath.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static string FormatTime(ClipEdit.Domain.Timeline.MediaTime value) =>
        value.TotalSeconds.ToString("0.###############", CultureInfo.InvariantCulture);
}
