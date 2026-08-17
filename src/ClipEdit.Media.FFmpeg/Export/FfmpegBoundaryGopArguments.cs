using ClipEdit.Media.Export;

namespace ClipEdit.Media.FFmpeg.Export;

internal static class FfmpegBoundaryGopArguments
{
    public static IReadOnlyList<string> CreateInteriorCopy(
        ExportPlan plan,
        string outputPath)
    {
        var segment = GetSegment(plan);
        var boundary = segment.BoundaryGopInfo!;
        var decodeDuration = boundary.CopiedEndDecodeTimestamp -
                             boundary.CopiedStartPresentationTimestamp;
        if (decodeDuration <= ClipEdit.Domain.Timeline.MediaTime.Zero)
        {
            throw new ExportPlanException("The Boundary-GOP interior has no copyable decode duration.");
        }

        return
        [
            "-hide_banner",
            "-nostdin",
            "-n",
            "-loglevel",
            "warning",
            "-ss",
            FfmpegExportArguments.FormatTime(boundary.CopiedStartPresentationTimestamp),
            "-t",
            FfmpegExportArguments.FormatTime(decodeDuration),
            "-i",
            segment.SourcePath,
            "-map",
            $"0:{segment.VideoStreamIndex}",
            "-an",
            "-c:v",
            "copy",
            "-map_metadata",
            "-1",
            "-map_chapters",
            "-1",
            "-f",
            FfmpegExportArguments.GetMuxer(plan.Preset.Container),
            outputPath,
        ];
    }

    public static IReadOnlyList<string> CreateFinalMux(
        ExportPlan plan,
        string manifestPath,
        string outputPath)
    {
        var segment = GetSegment(plan);
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
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            manifestPath,
        };
        var hasAudio = FfmpegExportArguments.HasAnyAudio(plan);
        if (hasAudio)
        {
            arguments.Add("-i");
            arguments.Add(segment.SourcePath);
            foreach (var externalSourcePath in FfmpegExportArguments.GetSequenceExternalAudioSources(plan))
            {
                arguments.Add("-i");
                arguments.Add(externalSourcePath);
            }
        }

        arguments.Add("-map");
        arguments.Add("0:v:0");
        if (hasAudio)
        {
            arguments.Add("-filter_complex");
            arguments.Add(FfmpegExportArguments.CreateVideoStreamCopyAudioFilterGraph(
                plan,
                usesSeparateAudioInput: true));
            arguments.Add("-map");
            arguments.Add("[aout]");
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.Add("-c:v");
        arguments.Add("copy");
        arguments.AddRange(FfmpegExportArguments.CreateAudioPresetArguments(plan));
        arguments.Add("-map_metadata");
        arguments.Add("-1");
        arguments.Add("-map_chapters");
        arguments.Add("-1");
        arguments.Add("-metadata:s:v:0");
        arguments.Add("rotate=0");
        if (plan.Preset.Container == ExportContainer.Mp4)
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }
        arguments.Add("-f");
        arguments.Add(FfmpegExportArguments.GetMuxer(plan.Preset.Container));
        arguments.Add(outputPath);
        return arguments;
    }

    private static ExportVideoSegmentPlan GetSegment(ExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Strategy != ExportStrategy.BoundaryGop ||
            !plan.IsSequence ||
            plan.VideoSegments.Length != 1 ||
            plan.VideoSegments[0].BoundaryGopInfo is null)
        {
            throw new ArgumentException("A Boundary-GOP plan is required.", nameof(plan));
        }

        return plan.VideoSegments[0];
    }
}
