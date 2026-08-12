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
        };

        if (plan.IsSequence)
        {
            foreach (var segment in plan.VideoSegments)
            {
                arguments.Add("-i");
                arguments.Add(segment.SourcePath);
            }

            foreach (var externalSourcePath in GetSequenceExternalAudioSources(plan))
            {
                arguments.Add("-i");
                arguments.Add(externalSourcePath);
            }
        }
        else
        {
            arguments.Add("-i");
            arguments.Add(plan.SourcePath);

            foreach (var externalSourcePath in GetExternalAudioSources(plan))
            {
                arguments.Add("-i");
                arguments.Add(externalSourcePath);
            }
        }

        arguments.Add("-filter_complex");
        arguments.Add(plan.IsSequence ? CreateSequenceFilterGraph(plan) : CreateFilterGraph(plan));
        arguments.Add("-map");
        arguments.Add("[vout]");

        if (HasAnyAudio(plan))
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

    internal static string CreateSequenceFilterGraph(ExportPlan plan)
    {
        if (!plan.IsSequence)
        {
            throw new ArgumentException("The plan is not a sequence export.", nameof(plan));
        }

        var filters = new List<string>();
        var hasEmbeddedAudio = plan.VideoSegments.Any(segment => !segment.AudioTracks.IsEmpty);
        for (var segmentIndex = 0; segmentIndex < plan.VideoSegments.Length; segmentIndex++)
        {
            var segment = plan.VideoSegments[segmentIndex];
            var range = segment.SourceRange;
            filters.Add(
                $"[{segmentIndex}:{segment.VideoStreamIndex}]" +
                $"trim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                $"setpts=PTS-STARTPTS,crop={segment.Crop.Width}:{segment.Crop.Height}:{segment.Crop.X}:{segment.Crop.Y}," +
                $"scale={plan.OutputSize.Width}:{plan.OutputSize.Height}:flags=lanczos,setsar=1[vseg{segmentIndex}]");

            if (!hasEmbeddedAudio)
            {
                continue;
            }

            if (segment.AudioTracks.IsEmpty)
            {
                filters.Add(
                    $"anullsrc=r=48000:cl=stereo,atrim=duration={FormatTime(range.Duration)}[aseg{segmentIndex}]");
                continue;
            }

            for (var trackIndex = 0; trackIndex < segment.AudioTracks.Length; trackIndex++)
            {
                var track = segment.AudioTracks[trackIndex];
                filters.Add(
                    $"[{segmentIndex}:{track.StreamIndex}]" +
                    CreateRangeMask(track) +
                    $"apad,atrim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                    "asetpts=PTS-STARTPTS,aresample=48000," +
                    "aformat=sample_fmts=fltp:channel_layouts=stereo," +
                    $"volume={FormatGain(track.GainDb)}dB[seg{segmentIndex}a{trackIndex}]");
            }

            if (segment.AudioTracks.Length == 1)
            {
                filters.Add($"[seg{segmentIndex}a0]anull[aseg{segmentIndex}]");
            }
            else
            {
                filters.Add(
                    string.Concat(Enumerable.Range(0, segment.AudioTracks.Length)
                        .Select(index => $"[seg{segmentIndex}a{index}]")) +
                    $"amix=inputs={segment.AudioTracks.Length}:duration=longest:normalize=0," +
                    $"alimiter=limit=0.95[aseg{segmentIndex}]");
            }
        }

        if (hasEmbeddedAudio)
        {
            filters.Add(
                string.Concat(Enumerable.Range(0, plan.VideoSegments.Length)
                    .Select(index => $"[vseg{index}][aseg{index}]")) +
                $"concat=n={plan.VideoSegments.Length}:v=1:a=1[vbase][abase]");
        }
        else
        {
            filters.Add(
                string.Concat(Enumerable.Range(0, plan.VideoSegments.Length)
                    .Select(index => $"[vseg{index}]")) +
                $"concat=n={plan.VideoSegments.Length}:v=1:a=0[vbase]");
        }

        var externalSources = GetSequenceExternalAudioSources(plan);
        var mixInputs = new List<string>();
        if (hasEmbeddedAudio)
        {
            mixInputs.Add("abase");
        }

        for (var trackIndex = 0; trackIndex < plan.AudioTracks.Length; trackIndex++)
        {
            var track = plan.AudioTracks[trackIndex];
            var inputIndex = plan.VideoSegments.Length +
                             GetExternalAudioSourceIndex(track, externalSources);
            var output = $"exta{trackIndex}";
            filters.Add(
                $"[{inputIndex}:{track.StreamIndex}]" +
                CreateRangeMask(track) +
                CreateDelay(track.TimelineOffset) +
                $"apad,atrim=start={FormatTime(plan.SequenceTimelineStart)}:" +
                $"end={FormatTime(plan.SequenceTimelineStart + plan.ExpectedDuration)},asetpts=PTS-STARTPTS," +
                "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo," +
                $"volume={FormatGain(track.GainDb)}dB[{output}]");
            mixInputs.Add(output);
        }

        filters.Add("[vbase]null[vout]");
        if (mixInputs.Count == 1)
        {
            filters.Add($"[{mixInputs[0]}]anull[aout]");
        }
        else if (mixInputs.Count > 1)
        {
            filters.Add(
                string.Concat(mixInputs.Select(input => $"[{input}]")) +
                $"amix=inputs={mixInputs.Count}:duration=longest:normalize=0," +
                "alimiter=limit=0.95[aout]");
        }

        return string.Join(';', filters);
    }

    internal static string CreateFilterGraph(ExportPlan plan)
    {
        var filters = new List<string>();
        var rangeCount = plan.SourceRanges.Length;
        var externalAudioSources = GetExternalAudioSources(plan);

        if (rangeCount > 1)
        {
            filters.Add(CreateSplit($"0:{plan.VideoStreamIndex}", "split", "vsrc", rangeCount));
            for (var trackIndex = 0; trackIndex < plan.AudioTracks.Length; trackIndex++)
            {
                var track = plan.AudioTracks[trackIndex];
                var inputIndex = GetAudioInputIndex(track, externalAudioSources);
                filters.Add(
                    $"[{inputIndex}:{track.StreamIndex}]" +
                    CreateRangeMask(track) +
                    CreateDelay(track.TimelineOffset) +
                    $"apad,asplit={rangeCount}" +
                    string.Concat(Enumerable.Range(0, rangeCount).Select(index => $"[asrc{trackIndex}_{index}]")));
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
                var track = plan.AudioTracks[trackIndex];
                var inputIndex = GetAudioInputIndex(track, externalAudioSources);
                var audioInput = rangeCount == 1
                    ? $"{inputIndex}:{track.StreamIndex}"
                    : $"asrc{trackIndex}_{index}";
                var inputPreparation = rangeCount == 1
                    ? CreateRangeMask(track) + CreateDelay(track.TimelineOffset) + "apad,"
                    : string.Empty;
                filters.Add(
                    $"[{audioInput}]{inputPreparation}atrim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
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

    private static IReadOnlyList<string> GetExternalAudioSources(ExportPlan plan)
    {
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return plan.AudioTracks
            .Where(track => track.ExternalSourcePath is not null)
            .Select(track => track.ExternalSourcePath!)
            .Distinct(pathComparer)
            .ToArray();
    }

    private static int GetAudioInputIndex(
        ExportAudioTrackPlan track,
        IReadOnlyList<string> externalAudioSources)
    {
        if (track.ExternalSourcePath is null)
        {
            return 0;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var index = 0; index < externalAudioSources.Count; index++)
        {
            if (string.Equals(track.ExternalSourcePath, externalAudioSources[index], comparison))
            {
                return index + 1;
            }
        }

        throw new ExportPlanException("An external audio source was not assigned an FFmpeg input.");
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

        if (HasAnyAudio(plan))
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

    private static bool HasAnyAudio(ExportPlan plan) =>
        plan.IsSequence
            ? plan.VideoSegments.Any(segment => !segment.AudioTracks.IsEmpty) || !plan.AudioTracks.IsEmpty
            : !plan.AudioTracks.IsEmpty;

    private static IReadOnlyList<string> GetSequenceExternalAudioSources(ExportPlan plan)
    {
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return plan.AudioTracks
            .Select(track => track.ExternalSourcePath!)
            .Distinct(pathComparer)
            .ToArray();
    }

    private static int GetExternalAudioSourceIndex(
        ExportAudioTrackPlan track,
        IReadOnlyList<string> externalAudioSources)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var index = 0; index < externalAudioSources.Count; index++)
        {
            if (string.Equals(track.ExternalSourcePath, externalAudioSources[index], comparison))
            {
                return index;
            }
        }

        throw new ExportPlanException("An external audio source was not assigned an FFmpeg input.");
    }

    private static string FormatTime(MediaTime value)
    {
        return value.TotalSeconds.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    private static string FormatGain(double gainDb)
    {
        return gainDb.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string CreateDelay(MediaTime timelineOffset)
    {
        if (timelineOffset < MediaTime.Zero)
        {
            throw new ExportPlanException("An audio timeline offset cannot be negative.");
        }

        return timelineOffset == MediaTime.Zero
            ? string.Empty
            : $"adelay=delays={FormatTime(timelineOffset)}s:all=1,";
    }

    private static string CreateRangeMask(ExportAudioTrackPlan track)
    {
        var audioEdit = track.AudioEdit;
        if (audioEdit is null || audioEdit.IsUnedited)
        {
            return string.Empty;
        }

        if (audioEdit.IsEmpty)
        {
            return "aeval='0':c=same,";
        }

        var keptExpression = string.Join(
            '+',
            audioEdit.KeptRanges.Select(range =>
                $"gte(t,{FormatTime(range.Start)})*lt(t,{FormatTime(range.End)})"));
        return $"aeval='if(gt({keptExpression},0),val(ch),0)':c=same,";
    }
}
