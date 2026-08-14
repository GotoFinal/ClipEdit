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
        var includeAudio = plan.Preset.SupportsAudio;
        var hasEmbeddedAudio = includeAudio &&
                               plan.VideoSegments.Any(segment => !segment.AudioTracks.IsEmpty);
        for (var segmentIndex = 0; segmentIndex < plan.VideoSegments.Length; segmentIndex++)
        {
            var segment = plan.VideoSegments[segmentIndex];
            var range = segment.SourceRange;
            if (segment.UsesCanvasTransform)
            {
                var transform = segment.CanvasTransform;
                var rotationRadians = $"{transform.RotationDegrees}*PI/180";
                var rotation = transform.RotationDegrees == 0
                    ? string.Empty
                    : $"format=rgba,rotate={rotationRadians}:" +
                      $"ow=rotw({rotationRadians}):oh=roth({rotationRadians}):c=black@0,";
                filters.Add(
                    $"[{segmentIndex}:{segment.VideoStreamIndex}]" +
                    $"trim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                    $"{CreateVideoSpeedFilter(segment.PlaybackSpeed)}," +
                    $"split=2[vseg{segmentIndex}basein][vseg{segmentIndex}contentin]");
                filters.Add(
                    $"[vseg{segmentIndex}basein]" +
                    $"scale={segment.CanvasSize.Width}:{segment.CanvasSize.Height}:flags=fast_bilinear," +
                    $"drawbox=c=black:t=fill[vseg{segmentIndex}base]");
                filters.Add(
                    $"[vseg{segmentIndex}contentin]" +
                    rotation +
                    $"scale=round(iw*{FormatScalar(transform.ScaleX)}):" +
                    $"round(ih*{FormatScalar(transform.ScaleY)}):flags=lanczos[vseg{segmentIndex}content]");
                filters.Add(
                    $"[vseg{segmentIndex}base][vseg{segmentIndex}content]" +
                    $"overlay=x=(W-w)/2{FormatSignedScalar(transform.OffsetX)}:" +
                    $"y=(H-h)/2{FormatSignedScalar(transform.OffsetY)}:shortest=1," +
                    $"crop={segment.CanvasCrop.Width}:{segment.CanvasCrop.Height}:" +
                    $"{segment.CanvasCrop.X}:{segment.CanvasCrop.Y}," +
                    $"scale={plan.OutputSize.Width}:{plan.OutputSize.Height}:flags=lanczos,format=yuv420p," +
                    $"setsar=1[vseg{segmentIndex}]");
            }
            else
            {
                filters.Add(
                    $"[{segmentIndex}:{segment.VideoStreamIndex}]" +
                    $"trim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                    $"{CreateVideoSpeedFilter(segment.PlaybackSpeed)}," +
                    $"crop={segment.Crop.Width}:{segment.Crop.Height}:{segment.Crop.X}:{segment.Crop.Y}," +
                    $"scale={plan.OutputSize.Width}:{plan.OutputSize.Height}:flags=lanczos,format=yuv420p,setsar=1[vseg{segmentIndex}]");
            }

            if (!hasEmbeddedAudio)
            {
                continue;
            }

            if (segment.AudioTracks.IsEmpty)
            {
                filters.Add(
                    $"anullsrc=r=48000:cl=stereo,atrim=duration={FormatTime(segment.TimelineDuration)},aformat=sample_fmts=fltp:channel_layouts=stereo[aseg{segmentIndex}]");
                continue;
            }

            for (var trackIndex = 0; trackIndex < segment.AudioTracks.Length; trackIndex++)
            {
                var track = segment.AudioTracks[trackIndex];
                filters.Add(
                    $"[{segmentIndex}:{track.StreamIndex}]" +
                    CreateRangeMask(track) +
                    $"apad,atrim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                    $"asetpts=PTS-STARTPTS,{CreateAudioSpeedFilter(segment.PlaybackSpeed)},aresample=48000," +
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

        var sequenceInputs = new List<(string Video, string? Audio)>();
        var sequenceCursor = plan.SequenceTimelineStart;
        var gapIndex = 0;
        for (var segmentIndex = 0; segmentIndex < plan.VideoSegments.Length; segmentIndex++)
        {
            var segmentStart = plan.GetVideoSegmentTimelineStart(segmentIndex);
            if (segmentStart > sequenceCursor)
            {
                AddGap(segmentStart - sequenceCursor);
            }

            sequenceInputs.Add(($"vseg{segmentIndex}", hasEmbeddedAudio ? $"aseg{segmentIndex}" : null));
            sequenceCursor = segmentStart + plan.VideoSegments[segmentIndex].TimelineDuration;
        }

        var sequenceEnd = plan.SequenceTimelineStart + plan.TimelineDuration;
        if (sequenceEnd > sequenceCursor)
        {
            AddGap(sequenceEnd - sequenceCursor);
        }

        if (hasEmbeddedAudio)
        {
            filters.Add(
                string.Concat(sequenceInputs.Select(input => $"[{input.Video}][{input.Audio}]")) +
                $"concat=n={sequenceInputs.Count}:v=1:a=1[vbase][abase]");
        }
        else
        {
            filters.Add(
                string.Concat(sequenceInputs.Select(input => $"[{input.Video}]")) +
                $"concat=n={sequenceInputs.Count}:v=1:a=0[vbase]");
        }

        void AddGap(MediaTime duration)
        {
            var videoLabel = $"vgap{gapIndex}";
            filters.Add(
                $"color=c=black:s={plan.OutputSize.Width}x{plan.OutputSize.Height}:" +
                $"r=30:d={FormatTime(duration)},format=yuv420p,setsar=1[{videoLabel}]");
            string? audioLabel = null;
            if (hasEmbeddedAudio)
            {
                audioLabel = $"agap{gapIndex}";
                filters.Add(
                    $"anullsrc=r=48000:cl=stereo,atrim=duration={FormatTime(duration)},aformat=sample_fmts=fltp:channel_layouts=stereo[{audioLabel}]");
            }

            sequenceInputs.Add((videoLabel, audioLabel));
            gapIndex++;
        }

        var externalSources = GetSequenceExternalAudioSources(plan);
        var mixInputs = new List<string>();
        if (hasEmbeddedAudio)
        {
            mixInputs.Add("abase");
        }

        for (var trackIndex = 0; includeAudio && trackIndex < plan.AudioTracks.Length; trackIndex++)
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
                $"end={FormatTime(plan.SequenceTimelineStart + plan.TimelineDuration)},asetpts=PTS-STARTPTS," +
                "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo," +
                $"volume={FormatGain(track.GainDb)}dB[{output}]");
            mixInputs.Add(output);
        }

        AddVideoOutputFilters(filters, "vbase", plan);
        if (mixInputs.Count == 1)
        {
            if (plan.EncodingSettings.PlaybackSpeedPercent == 100)
            {
                filters.Add($"[{mixInputs[0]}]anull[aout]");
            }
            else
            {
                AddAudioOutputFilters(filters, mixInputs[0], plan);
            }
        }
        else if (mixInputs.Count > 1)
        {
            var mixedOutput = plan.EncodingSettings.PlaybackSpeedPercent == 100 ? "aout" : "amixed";
            filters.Add(
                string.Concat(mixInputs.Select(input => $"[{input}]")) +
                $"amix=inputs={mixInputs.Count}:duration=longest:normalize=0," +
                $"alimiter=limit=0.95[{mixedOutput}]");
            if (mixedOutput == "amixed")
            {
                AddAudioOutputFilters(filters, mixedOutput, plan);
            }
        }

        return string.Join(';', filters);
    }

    internal static string CreateFilterGraph(ExportPlan plan)
    {
        var filters = new List<string>();
        var rangeCount = plan.SourceRanges.Length;
        var externalAudioSources = GetExternalAudioSources(plan);
        var audioTracks = plan.Preset.SupportsAudio ? plan.AudioTracks : [];

        if (rangeCount > 1)
        {
            filters.Add(CreateSplit($"0:{plan.VideoStreamIndex}", "split", "vsrc", rangeCount));
            for (var trackIndex = 0; trackIndex < audioTracks.Length; trackIndex++)
            {
                var track = audioTracks[trackIndex];
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
            var videoOutput = $"vseg{index}";
            filters.Add(
                $"[{videoInput}]trim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                $"setpts=PTS-STARTPTS,crop={plan.Crop.Width}:{plan.Crop.Height}:{plan.Crop.X}:{plan.Crop.Y}," +
                $"setsar=1[{videoOutput}]");

            for (var trackIndex = 0; trackIndex < audioTracks.Length; trackIndex++)
            {
                var track = audioTracks[trackIndex];
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
                $"concat=n={rangeCount}:v=1:a=0[vbase]");
        }
        else
        {
            filters.Add("[vseg0]null[vbase]");
        }

        AddVideoOutputFilters(filters, "vbase", plan);

        for (var trackIndex = 0; trackIndex < audioTracks.Length; trackIndex++)
        {
            var trackInput = $"aseg{trackIndex}_0";
            if (rangeCount > 1)
            {
                trackInput = $"atrack{trackIndex}";
                filters.Add(
                    string.Concat(Enumerable.Range(0, rangeCount).Select(index => $"[aseg{trackIndex}_{index}]")) +
                    $"concat=n={rangeCount}:v=0:a=1[{trackInput}]");
            }

            var mixedInput = audioTracks.Length == 1
                ? plan.EncodingSettings.PlaybackSpeedPercent == 100 ? "aout" : "amixed"
                : $"amixin{trackIndex}";
            var conform = audioTracks.Length == 1
                ? string.Empty
                : "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,";
            filters.Add(
                $"[{trackInput}]{conform}volume={FormatGain(audioTracks[trackIndex].GainDb)}dB[{mixedInput}]");
        }

        if (audioTracks.Length > 1)
        {
            var mixedOutput = plan.EncodingSettings.PlaybackSpeedPercent == 100 ? "aout" : "amixed";
            filters.Add(
                string.Concat(Enumerable.Range(0, audioTracks.Length).Select(index => $"[amixin{index}]")) +
                $"amix=inputs={audioTracks.Length}:duration=longest:normalize=0,alimiter=limit=0.95[{mixedOutput}]");
        }

        if (audioTracks.Length > 0 && plan.EncodingSettings.PlaybackSpeedPercent != 100)
        {
            AddAudioOutputFilters(filters, "amixed", plan);
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
        if (!plan.Preset.SupportsAudio)
        {
            return [];
        }

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
        var quality = plan.EncodingSettings.Quality;
        var arguments = plan.Preset.VideoCodec switch
        {
            VideoCodecFamily.H264 => new List<string>
            {
                "-c:v", "libx264", "-preset", "medium", "-crf", MapQualityAroundDefault(quality, 36, 20, 16).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
            },
            VideoCodecFamily.Vp9 =>
            [
                "-c:v", "libvpx-vp9", "-crf", MapQualityAroundDefault(quality, 50, 30, 20).ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-row-mt", "1", "-pix_fmt", "yuv420p",
            ],
            VideoCodecFamily.Gif => ["-c:v", "gif", "-loop", "0"],
            _ => throw new ExportPlanException($"Unsupported video codec family: {plan.Preset.VideoCodec}."),
        };
        if (plan.Preset.VideoBitRateBitsPerSecond is { } videoBitRate)
        {
            RemoveOption(arguments, "-crf");
            RemoveOption(arguments, "-b:v");
            arguments.Add("-b:v");
            arguments.Add(ScaleBitRate(videoBitRate, quality).ToString(CultureInfo.InvariantCulture));
        }
        if (plan.Preset.FrameRate is { } frameRate)
        {
            arguments.Add("-r");
            arguments.Add($"{frameRate.Numerator}/{frameRate.Denominator}");
        }

        var audioBitRate = ScaleBitRate(
                plan.Preset.AudioBitRateBitsPerSecond ??
                (plan.Preset.AudioCodec == AudioCodecFamily.Aac ? 192_000 : 160_000),
                quality)
            .ToString(CultureInfo.InvariantCulture);

        if (HasAnyAudio(plan))
        {
            arguments.AddRange(plan.Preset.AudioCodec switch
            {
                AudioCodecFamily.Aac => ["-c:a", "aac", "-b:a", audioBitRate],
                AudioCodecFamily.Opus => ["-c:a", "libopus", "-b:a", audioBitRate],
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

    private static void RemoveOption(List<string> arguments, string option)
    {
        var index = arguments.IndexOf(option);
        if (index < 0)
        {
            return;
        }

        arguments.RemoveAt(index);
        arguments.RemoveAt(index);
    }

    private static string GetMuxer(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => "mp4",
        ExportContainer.WebM => "webm",
        ExportContainer.Matroska => "matroska",
        ExportContainer.Gif => "gif",
        _ => throw new ArgumentOutOfRangeException(nameof(container), container, "Unsupported output container."),
    };

    private static bool HasAnyAudio(ExportPlan plan) =>
        plan.Preset.SupportsAudio && (plan.IsSequence
            ? plan.VideoSegments.Any(segment => !segment.AudioTracks.IsEmpty) || !plan.AudioTracks.IsEmpty
            : !plan.AudioTracks.IsEmpty);

    private static IReadOnlyList<string> GetSequenceExternalAudioSources(ExportPlan plan)
    {
        if (!plan.Preset.SupportsAudio)
        {
            return [];
        }

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

    private static string FormatScalar(double value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static string FormatSignedScalar(double value)
    {
        return value >= 0 ? $"+{FormatScalar(value)}" : FormatScalar(value);
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

    private static void AddVideoOutputFilters(
        ICollection<string> filters,
        string input,
        ExportPlan plan)
    {
        var speed = plan.EncodingSettings.PlaybackSpeedPercent == 100
            ? string.Empty
            : $"setpts=(PTS-STARTPTS)/{FormatScalar(plan.EncodingSettings.PlaybackSpeed)},";
        if (plan.Preset.VideoCodec != VideoCodecFamily.Gif)
        {
            filters.Add(
                $"[{input}]{speed}scale={plan.OutputSize.Width}:{plan.OutputSize.Height}:flags=lanczos," +
                "format=yuv420p,setsar=1[vout]");
            return;
        }

        var maximumColors = MapQuality(plan.EncodingSettings.Quality, 32, 256);
        filters.Add(
            $"[{input}]{speed}fps={plan.EncodingSettings.GifFrameRate}," +
            $"scale={plan.OutputSize.Width}:{plan.OutputSize.Height}:flags=lanczos," +
            "split=2[gifsource][gifpaletteinput]");
        filters.Add(
            $"[gifpaletteinput]palettegen=max_colors={maximumColors}:stats_mode=diff[gifpalette]");
        filters.Add(
            "[gifsource][gifpalette]paletteuse=dither=sierra2_4a:diff_mode=rectangle[vout]");
    }

    private static void AddAudioOutputFilters(
        ICollection<string> filters,
        string input,
        ExportPlan plan)
    {
        filters.Add(plan.EncodingSettings.PlaybackSpeedPercent == 100
            ? $"[{input}]anull[aout]"
            : $"[{input}]{CreateAudioSpeedFilter(plan.EncodingSettings.PlaybackSpeed)}[aout]");
    }

    private static string CreateVideoSpeedFilter(double speed) =>
        speed == 1
            ? "setpts=PTS-STARTPTS"
            : $"setpts=(PTS-STARTPTS)/{FormatScalar(speed)}";

    private static string CreateAudioSpeedFilter(double speed)
    {
        if (speed == 1)
        {
            return "anull";
        }

        var stages = new List<double>();
        var remaining = speed;
        while (remaining > 2)
        {
            stages.Add(2);
            remaining /= 2;
        }
        while (remaining < 0.5)
        {
            stages.Add(0.5);
            remaining /= 0.5;
        }
        stages.Add(remaining);
        return string.Join(',', stages.Select(stage => $"atempo={FormatScalar(stage)}"));
    }

    private static int MapQuality(int quality, int lowQualityValue, int highQualityValue)
    {
        var progress = (quality - 1) / 99d;
        return (int)Math.Round(
            lowQualityValue + ((highQualityValue - lowQualityValue) * progress),
            MidpointRounding.AwayFromZero);
    }

    private static int MapQualityAroundDefault(
        int quality,
        int lowQualityValue,
        int defaultValue,
        int highQualityValue)
    {
        if (quality <= ExportEncodingSettings.DefaultQuality)
        {
            var progress = (quality - 1) / (double)(ExportEncodingSettings.DefaultQuality - 1);
            return (int)Math.Round(
                lowQualityValue + ((defaultValue - lowQualityValue) * progress),
                MidpointRounding.AwayFromZero);
        }

        var highProgress = (quality - ExportEncodingSettings.DefaultQuality) /
                           (double)(100 - ExportEncodingSettings.DefaultQuality);
        return (int)Math.Round(
            defaultValue + ((highQualityValue - defaultValue) * highProgress),
            MidpointRounding.AwayFromZero);
    }

    private static long ScaleBitRate(long bitRate, int quality)
    {
        var factor = quality <= ExportEncodingSettings.DefaultQuality
            ? 0.25d + (((quality - 1) / 74d) * 0.75d)
            : 1d + (((quality - ExportEncodingSettings.DefaultQuality) / 25d) * 0.5d);
        return Math.Max(1, (long)Math.Round(bitRate * factor, MidpointRounding.AwayFromZero));
    }
}
