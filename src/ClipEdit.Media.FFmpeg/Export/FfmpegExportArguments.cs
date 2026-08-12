using System.Globalization;
using System.Text;
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

        if (plan.AudioStreamIndex is not null)
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
        var graph = new StringBuilder();
        var rangeCount = plan.SourceRanges.Length;
        var hasAudio = plan.AudioStreamIndex is not null;

        if (rangeCount > 1)
        {
            AppendSplit(graph, $"0:{plan.VideoStreamIndex}", "split", "vsrc", rangeCount);
            if (hasAudio)
            {
                AppendSplit(graph, $"0:{plan.AudioStreamIndex!.Value}", "asplit", "asrc", rangeCount);
            }
        }

        for (var index = 0; index < rangeCount; index++)
        {
            var range = plan.SourceRanges[index];
            var videoInput = rangeCount == 1 ? $"0:{plan.VideoStreamIndex}" : $"vsrc{index}";
            var videoOutput = rangeCount == 1 ? "vout" : $"vseg{index}";
            graph.Append('[').Append(videoInput).Append(']')
                .Append("trim=start=").Append(FormatTime(range.Start))
                .Append(":end=").Append(FormatTime(range.End))
                .Append(",setpts=PTS-STARTPTS")
                .Append(",crop=").Append(plan.Crop.Width)
                .Append(':').Append(plan.Crop.Height)
                .Append(':').Append(plan.Crop.X)
                .Append(':').Append(plan.Crop.Y)
                .Append(",setsar=1")
                .Append('[').Append(videoOutput).Append("]; ");

            if (hasAudio)
            {
                var audioInput = rangeCount == 1 ? $"0:{plan.AudioStreamIndex!.Value}" : $"asrc{index}";
                var audioOutput = rangeCount == 1 ? "aout" : $"aseg{index}";
                graph.Append('[').Append(audioInput).Append(']')
                    .Append("atrim=start=").Append(FormatTime(range.Start))
                    .Append(":end=").Append(FormatTime(range.End))
                    .Append(",asetpts=PTS-STARTPTS")
                    .Append('[').Append(audioOutput).Append("]; ");
            }
        }

        if (rangeCount > 1)
        {
            for (var index = 0; index < rangeCount; index++)
            {
                graph.Append("[vseg").Append(index).Append(']');
                if (hasAudio)
                {
                    graph.Append("[aseg").Append(index).Append(']');
                }
            }

            graph.Append("concat=n=").Append(rangeCount)
                .Append(hasAudio ? ":v=1:a=1[vout][aout]" : ":v=1:a=0[vout]");
        }
        else
        {
            graph.Length -= 2;
        }

        return graph.ToString();
    }

    private static void AppendSplit(
        StringBuilder graph,
        string input,
        string filter,
        string outputPrefix,
        int count)
    {
        graph.Append('[').Append(input).Append(']')
            .Append(filter).Append('=').Append(count);
        for (var index = 0; index < count; index++)
        {
            graph.Append('[').Append(outputPrefix).Append(index).Append(']');
        }

        graph.Append("; ");
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

        if (plan.AudioStreamIndex is not null)
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
}
