using System.Globalization;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;

namespace ClipEdit.Media.FFmpeg.Export;

internal static class FfmpegExportArguments
{
    public static IReadOnlyList<string> Create(ExportPlan plan, string temporaryOutputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryOutputPath);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-n",
            "-loglevel",
            "warning",
            "-progress",
            "pipe:1",
            "-nostats",
            "-i",
            plan.SourcePath,
            "-filter_complex",
            CreateFilterGraph(plan),
            "-map",
            "[vout]",
        };

        if (!plan.AudioTracks.IsEmpty)
        {
            arguments.Add("-map");
            arguments.Add("[aout]");
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.AddRange(CreatePresetArguments(plan));
        arguments.Add("-map_metadata");
        arguments.Add("-1");
        arguments.Add("-map_chapters");
        arguments.Add("-1");
        arguments.Add("-metadata:s:v:0");
        arguments.Add("rotate=0");
        arguments.Add("-f");
        arguments.Add(GetMuxer(plan.Preset.Container));
        arguments.Add(temporaryOutputPath);
        return arguments;
    }

    internal static string CreateFilterGraph(ExportPlan plan)
    {
        var filters = new List<string>();
        var rangeCount = plan.SourceRanges.Length;

        if (rangeCount > 1)
        {
            filters.Add(CreateSplit($"0:{plan.VideoStreamIndex}", "split", "vsrc", rangeCount));
            for (var trackIndex = 0; trackIndex < plan.AudioTracks.Length; trackIndex++)
            {
                filters.Add(CreateSplit(
                    $"0:{plan.AudioTracks[trackIndex].StreamIndex}",
                    "asplit",
                    $"asrc{trackIndex}_",
                    rangeCount));
            }
        }

        for (var index = 0; index < rangeCount; index++)
        {
            var range = plan.SourceRanges[index];
            var videoInput = rangeCount == 1 ? $"0:{plan.VideoStreamIndex}" : $"vsrc{index}";
            var videoOutput = rangeCount == 1 ? "vout" : $"vseg{index}";
            filters.Add(
                $"[{videoInput}]trim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                $"setpts=PTS-STARTPTS,crop={plan.Crop.Width}:{plan.Crop.Height}:{plan.Crop.X}:{plan.Crop.Y}," +
                $"setsar=1[{videoOutput}]");

            for (var trackIndex = 0; trackIndex < plan.AudioTracks.Length; trackIndex++)
            {
                var streamIndex = plan.AudioTracks[trackIndex].StreamIndex;
                var audioInput = rangeCount == 1 ? $"0:{streamIndex}" : $"asrc{trackIndex}_{index}";
                filters.Add(
                    $"[{audioInput}]atrim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                    $"asetpts=PTS-STARTPTS[aseg{trackIndex}_{index}]");
            }
        }

        if (rangeCount > 1)
        {
            filters.Add(
                string.Concat(Enumerable.Range(0, rangeCount).Select(index => $"[vseg{index}]")) +
                $"concat=n={rangeCount}:v=1:a=0[vout]");
        }

        for (var trackIndex = 0; trackIndex < plan.AudioTracks.Length; trackIndex++)
        {
            var trackInput = $"aseg{trackIndex}_0";
            if (rangeCount > 1)
            {
                trackInput = $"atrack{trackIndex}";
                filters.Add(
                    string.Concat(Enumerable.Range(0, rangeCount).Select(index => $"[aseg{trackIndex}_{index}]")) +
                    $"concat=n={rangeCount}:v=0:a=1[{trackInput}]");
            }

            var mixedInput = plan.AudioTracks.Length == 1 ? "aout" : $"amixin{trackIndex}";
            var conform = plan.AudioTracks.Length == 1
                ? string.Empty
                : "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,";
            filters.Add(
                $"[{trackInput}]{conform}volume={FormatGain(plan.AudioTracks[trackIndex].GainDb)}dB[{mixedInput}]");
        }

        if (plan.AudioTracks.Length > 1)
        {
            filters.Add(
                string.Concat(Enumerable.Range(0, plan.AudioTracks.Length).Select(index => $"[amixin{index}]")) +
                $"amix=inputs={plan.AudioTracks.Length}:duration=longest:normalize=0,alimiter=limit=0.95[aout]");
        }

        return string.Join(';', filters);
    }

    private static string CreateSplit(
        string input,
        string filter,
        string outputPrefix,
        int count)
    {
        return $"[{input}]{filter}={count}" +
               string.Concat(Enumerable.Range(0, count).Select(index => $"[{outputPrefix}{index}]"));
    }

    private static IEnumerable<string> CreatePresetArguments(ExportPlan plan)
    {
        var arguments = plan.Preset.VideoCodec switch
        {
            VideoCodecFamily.H264 => new List<string>
            {
                "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            },
            VideoCodecFamily.Vp9 =>
            [
                "-c:v", "libvpx-vp9", "-crf", "30", "-b:v", "0", "-row-mt", "1", "-pix_fmt", "yuv420p",
            ],
            _ => throw new ExportPlanException($"Unsupported video codec family: {plan.Preset.VideoCodec}."),
        };

        if (!plan.AudioTracks.IsEmpty)
        {
            arguments.AddRange(plan.Preset.AudioCodec switch
            {
                AudioCodecFamily.Aac => ["-c:a", "aac", "-b:a", "192k"],
                AudioCodecFamily.Opus => ["-c:a", "libopus", "-b:a", "160k"],
                _ => throw new ExportPlanException($"Unsupported audio codec family: {plan.Preset.AudioCodec}."),
            });
        }

        if (plan.Preset.Container == ExportContainer.Mp4)
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }

        return arguments;
    }

    private static string GetMuxer(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => "mp4",
        ExportContainer.WebM => "webm",
        _ => throw new ArgumentOutOfRangeException(nameof(container), container, "Unsupported output container."),
    };

    private static string FormatTime(MediaTime value)
    {
        return value.TotalSeconds.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    private static string FormatGain(double gainDb)
    {
        return gainDb.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
