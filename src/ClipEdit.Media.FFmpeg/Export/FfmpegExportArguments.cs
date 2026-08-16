using System.Globalization;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;

namespace ClipEdit.Media.FFmpeg.Export;

internal static class FfmpegExportArguments
{
    public static IReadOnlyList<string> Create(ExportPlan plan, string temporaryOutputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryOutputPath);

        if (plan.Strategy == ExportStrategy.StreamCopy)
        {
            return CreateStreamCopy(plan, temporaryOutputPath);
        }
        if (plan.Strategy == ExportStrategy.VideoStreamCopy)
        {
            return CreateVideoStreamCopy(plan, temporaryOutputPath);
        }

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

    private static IReadOnlyList<string> CreateStreamCopy(
        ExportPlan plan,
        string temporaryOutputPath)
    {
        var segment = plan.VideoSegments.Single();
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
            segment.SourcePath,
            "-map",
            $"0:{segment.VideoStreamIndex}",
        };
        if (plan.Preset.SupportsAudio && segment.AudioTracks.Length == 1)
        {
            arguments.Add("-map");
            arguments.Add($"0:{segment.AudioTracks[0].StreamIndex}");
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.Add("-c");
        arguments.Add("copy");
        arguments.Add("-map_metadata");
        arguments.Add("-1");
        arguments.Add("-map_chapters");
        arguments.Add("-1");
        if (plan.Preset.Container == ExportContainer.Mp4)
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }
        arguments.Add("-f");
        arguments.Add(GetMuxer(plan.Preset.Container));
        arguments.Add(temporaryOutputPath);
        return arguments;
    }

    private static IReadOnlyList<string> CreateVideoStreamCopy(
        ExportPlan plan,
        string temporaryOutputPath)
    {
        var segment = plan.VideoSegments.Single();
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
            segment.SourcePath,
        };
        foreach (var externalSourcePath in GetSequenceExternalAudioSources(plan))
        {
            arguments.Add("-i");
            arguments.Add(externalSourcePath);
        }

        arguments.Add("-map");
        arguments.Add($"0:{segment.VideoStreamIndex}");
        if (HasAnyAudio(plan))
        {
            arguments.Add("-filter_complex");
            arguments.Add(CreateVideoStreamCopyAudioFilterGraph(plan));
            arguments.Add("-map");
            arguments.Add("[aout]");
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.Add("-c:v");
        arguments.Add("copy");
        arguments.AddRange(CreateAudioPresetArguments(plan));
        arguments.Add("-map_metadata");
        arguments.Add("-1");
        arguments.Add("-map_chapters");
        arguments.Add("-1");
        if (plan.Preset.Container == ExportContainer.Mp4)
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }
        arguments.Add("-f");
        arguments.Add(GetMuxer(plan.Preset.Container));
        arguments.Add(temporaryOutputPath);
        return arguments;
    }

    internal static string CreateVideoStreamCopyAudioFilterGraph(ExportPlan plan)
    {
        if (!plan.IsSequence || plan.Strategy != ExportStrategy.VideoStreamCopy)
        {
            throw new ArgumentException("The plan is not a video-stream-copy export.", nameof(plan));
        }

        var segment = plan.VideoSegments.Single();
        var range = segment.SourceRange;
        var filters = new List<string>();
        var mixInputs = new List<string>();
        for (var trackIndex = 0; trackIndex < segment.AudioTracks.Length; trackIndex++)
        {
            var track = segment.AudioTracks[trackIndex];
            var output = $"emb{trackIndex}";
            filters.Add(
                $"[0:{track.StreamIndex}]" +
                CreateRangeMask(track) +
                $"apad,atrim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                "asetpts=PTS-STARTPTS,aresample=48000," +
                "aformat=sample_fmts=fltp:channel_layouts=stereo," +
                $"volume={FormatGain(track.GainDb)}dB[{output}]");
            mixInputs.Add(output);
        }

        var externalSources = GetSequenceExternalAudioSources(plan);
        for (var trackIndex = 0; trackIndex < plan.AudioTracks.Length; trackIndex++)
        {
            var track = plan.AudioTracks[trackIndex];
            var inputIndex = 1 + GetExternalAudioSourceIndex(track, externalSources);
            var output = $"ext{trackIndex}";
            filters.Add(
                $"[{inputIndex}:{track.StreamIndex}]" +
                CreateRangeMask(track) +
                CreateDelay(track.TimelineOffset) +
                $"apad,atrim=start={FormatTime(plan.SequenceTimelineStart)}:" +
                $"end={FormatTime(plan.SequenceTimelineStart + plan.TimelineDuration)}," +
                "asetpts=PTS-STARTPTS,aresample=48000," +
                "aformat=sample_fmts=fltp:channel_layouts=stereo," +
                $"volume={FormatGain(track.GainDb)}dB[{output}]");
            mixInputs.Add(output);
        }

        if (mixInputs.Count == 1)
        {
            filters.Add($"[{mixInputs[0]}]anull[aout]");
        }
        else
        {
            filters.Add(
                string.Concat(mixInputs.Select(input => $"[{input}]")) +
                $"amix=inputs={mixInputs.Count}:duration=longest:normalize=0," +
                "alimiter=limit=0.95[aout]");
        }

        return string.Join(';', filters);
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
        var videoPixelFormat = GetOutputPixelFormat(plan);
        var overlayPixelFormat = plan.PreservesHdr ? "yuv420p10" : "yuv420";
        for (var segmentIndex = 0; segmentIndex < plan.VideoSegments.Length; segmentIndex++)
        {
            var segment = plan.VideoSegments[segmentIndex];
            var range = segment.SourceRange;
            var inputColorConversion = CreateInputColorConversion(segment.VideoColorInfo, plan);
            if (segment.UsesCanvasTransform)
            {
                AddCanvasSegmentVideoFilters(
                    filters,
                    plan,
                    segment,
                    segmentIndex,
                    inputColorConversion,
                    videoPixelFormat,
                    overlayPixelFormat);
            }
            else
            {
                filters.Add(
                    $"[{segmentIndex}:{segment.VideoStreamIndex}]" +
                    $"trim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
                    $"{CreateVideoSpeedFilter(segment.PlaybackSpeed)}," +
                    inputColorConversion +
                    $"crop={segment.Crop.Width}:{segment.Crop.Height}:{segment.Crop.X}:{segment.Crop.Y}," +
                    $"scale={plan.OutputSize.Width}:{plan.OutputSize.Height}:flags=lanczos,format={videoPixelFormat},setsar=1[vseg{segmentIndex}]");
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
            var colorProperties = plan.PreservesHdr
                ? $",{CreateHdrSetParameters(plan.OutputVideoColorInfo!)}"
                : string.Empty;
            filters.Add(
                $"color=c=black:s={plan.OutputSize.Width}x{plan.OutputSize.Height}:" +
                $"r=30:d={FormatTime(duration)},format={videoPixelFormat}{colorProperties}," +
                $"setsar=1[{videoLabel}]");
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

        AddSequenceVideoOutputFilters(filters, "vbase", plan);
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

    private static void AddCanvasSegmentVideoFilters(
        ICollection<string> filters,
        ExportPlan plan,
        ExportVideoSegmentPlan segment,
        int segmentIndex,
        string inputColorConversion,
        string videoPixelFormat,
        string overlayPixelFormat)
    {
        var range = segment.SourceRange;
        var transform = segment.CanvasTransform;
        var inputPreparation =
            $"[{segmentIndex}:{segment.VideoStreamIndex}]" +
            $"trim=start={FormatTime(range.Start)}:end={FormatTime(range.End)}," +
            $"{CreateVideoSpeedFilter(segment.PlaybackSpeed)}," +
            inputColorConversion;
        var mirroring =
            (transform.IsHorizontallyMirrored ? "hflip," : string.Empty) +
            (transform.IsVerticallyMirrored ? "vflip," : string.Empty);
        var rotation = CreateCanvasRotationFilter(transform.RotationDegrees, plan.PreservesHdr);

        if (TryCreateAxisAlignedCanvasComposition(segment, out var composition))
        {
            var direct = inputPreparation + mirroring + rotation;
            if (transform.ScaleX != 1 || transform.ScaleY != 1)
            {
                direct += CreateCanvasScaleFilter(transform.ScaleX, transform.ScaleY);
            }
            if (!composition.UsesCompleteContent)
            {
                direct +=
                    $"crop={composition.CropWidth}:{composition.CropHeight}:" +
                    $"{composition.CropX}:{composition.CropY},";
            }
            if (composition.RequiresPadding)
            {
                direct +=
                    $"pad={segment.CanvasCrop.Width}:{segment.CanvasCrop.Height}:" +
                    $"{composition.PadX}:{composition.PadY}:color=black,";
            }

            filters.Add(
                direct +
                CreateSegmentOutputConformance(plan, segment.CanvasCrop, videoPixelFormat) +
                $"[vseg{segmentIndex}]");
            return;
        }

        filters.Add(
            inputPreparation +
            $"split=2[vseg{segmentIndex}basein][vseg{segmentIndex}contentin]");
        var seedWidth = Math.Min(2, segment.CanvasSize.Width);
        var seedHeight = Math.Min(2, segment.CanvasSize.Height);
        filters.Add(
            $"[vseg{segmentIndex}basein]" +
            $"scale={seedWidth}:{seedHeight}:flags=fast_bilinear," +
            "drawbox=c=black:t=fill," +
            $"pad={segment.CanvasSize.Width}:{segment.CanvasSize.Height}:0:0:color=black" +
            $"[vseg{segmentIndex}base]");
        filters.Add(
            $"[vseg{segmentIndex}contentin]" +
            mirroring +
            rotation +
            CreateCanvasScaleFilter(transform.ScaleX, transform.ScaleY) +
            $"[vseg{segmentIndex}content]");
        filters.Add(
            $"[vseg{segmentIndex}base][vseg{segmentIndex}content]" +
            $"overlay=x=(W-w)/2{FormatSignedScalar(transform.OffsetX)}:" +
            $"y=(H-h)/2{FormatSignedScalar(transform.OffsetY)}:shortest=1:" +
            $"format={overlayPixelFormat}," +
            $"crop={segment.CanvasCrop.Width}:{segment.CanvasCrop.Height}:" +
            $"{segment.CanvasCrop.X}:{segment.CanvasCrop.Y}," +
            CreateSegmentOutputConformance(plan, segment.CanvasCrop, videoPixelFormat) +
            $"[vseg{segmentIndex}]");
    }

    private static string CreateCanvasRotationFilter(int rotationDegrees, bool preservesHdr)
    {
        return rotationDegrees switch
        {
            0 => string.Empty,
            90 => "transpose=dir=clock,",
            180 => "hflip,vflip,",
            270 => "transpose=dir=cclock,",
            _ => CreateArbitraryCanvasRotationFilter(rotationDegrees, preservesHdr),
        };
    }

    private static string CreateArbitraryCanvasRotationFilter(int rotationDegrees, bool preservesHdr)
    {
        var rotationRadians = $"{rotationDegrees}*PI/180";
        return $"format={(preservesHdr ? "rgba64le" : "rgba")},rotate={rotationRadians}:" +
               $"ow=rotw({rotationRadians}):oh=roth({rotationRadians}):c=black@0,";
    }

    private static string CreateCanvasScaleFilter(double scaleX, double scaleY) =>
        $"scale=round(iw*{FormatScalar(scaleX)}):" +
        $"round(ih*{FormatScalar(scaleY)}):flags=lanczos,";

    private static string CreateSegmentOutputConformance(
        ExportPlan plan,
        CropRegion canvasCrop,
        string videoPixelFormat)
    {
        var scale = canvasCrop.ExportSize == plan.OutputSize
            ? string.Empty
            : $"scale={plan.OutputSize.Width}:{plan.OutputSize.Height}:flags=lanczos,";
        return scale + $"format={videoPixelFormat},setsar=1";
    }

    private static bool TryCreateAxisAlignedCanvasComposition(
        ExportVideoSegmentPlan segment,
        out AxisAlignedCanvasComposition composition)
    {
        composition = default;
        var transform = segment.CanvasTransform;
        if (transform.RotationDegrees % 90 != 0 || segment.SourceSize is not { } sourceSize)
        {
            return false;
        }

        var swapsAxes = transform.RotationDegrees is 90 or 270;
        var rotatedWidth = swapsAxes ? sourceSize.Height : sourceSize.Width;
        var rotatedHeight = swapsAxes ? sourceSize.Width : sourceSize.Height;
        if (!TryRoundPositiveDimension(rotatedWidth * transform.ScaleX, out var contentWidth) ||
            !TryRoundPositiveDimension(rotatedHeight * transform.ScaleY, out var contentHeight) ||
            !TryRoundCoordinate(
                (segment.CanvasSize.Width - contentWidth) / 2d + transform.OffsetX,
                out var contentCanvasX) ||
            !TryRoundCoordinate(
                (segment.CanvasSize.Height - contentHeight) / 2d + transform.OffsetY,
                out var contentCanvasY))
        {
            return false;
        }

        var crop = segment.CanvasCrop;
        var intersectionLeft = Math.Max((long)crop.X, contentCanvasX);
        var intersectionTop = Math.Max((long)crop.Y, contentCanvasY);
        var intersectionRight = Math.Min(
            (long)crop.X + crop.Width,
            (long)contentCanvasX + contentWidth);
        var intersectionBottom = Math.Min(
            (long)crop.Y + crop.Height,
            (long)contentCanvasY + contentHeight);
        if (intersectionRight <= intersectionLeft || intersectionBottom <= intersectionTop)
        {
            return false;
        }

        var sourceX = checked((int)(intersectionLeft - contentCanvasX));
        var sourceY = checked((int)(intersectionTop - contentCanvasY));
        var width = checked((int)(intersectionRight - intersectionLeft));
        var height = checked((int)(intersectionBottom - intersectionTop));
        var padX = checked((int)(intersectionLeft - crop.X));
        var padY = checked((int)(intersectionTop - crop.Y));
        if (!AreChromaAligned(
                contentWidth,
                contentHeight,
                sourceX,
                sourceY,
                width,
                height,
                padX,
                padY,
                crop.Width,
                crop.Height))
        {
            return false;
        }

        composition = new AxisAlignedCanvasComposition(
            sourceX,
            sourceY,
            width,
            height,
            padX,
            padY,
            sourceX == 0 && sourceY == 0 && width == contentWidth && height == contentHeight,
            padX != 0 || padY != 0 || width != crop.Width || height != crop.Height);
        return true;
    }

    private static bool TryRoundPositiveDimension(double value, out int result)
    {
        result = 0;
        if (!double.IsFinite(value) || value < 1 || value > int.MaxValue)
        {
            return false;
        }

        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < 1 || rounded > int.MaxValue)
        {
            return false;
        }

        result = (int)rounded;
        return true;
    }

    private static bool TryRoundCoordinate(double value, out int result)
    {
        result = 0;
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
        {
            return false;
        }

        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (Math.Abs(value - rounded) > 0.000_001)
        {
            return false;
        }

        result = (int)rounded;
        return true;
    }

    private static bool AreChromaAligned(params int[] values) =>
        values.All(value => value % 2 == 0);

    private readonly record struct AxisAlignedCanvasComposition(
        int CropX,
        int CropY,
        int CropWidth,
        int CropHeight,
        int PadX,
        int PadY,
        bool UsesCompleteContent,
        bool RequiresPadding);

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
                $"setpts=PTS-STARTPTS,{CreateInputColorConversion(plan.SourceVideoColorInfo, plan)}" +
                $"crop={plan.Crop.Width}:{plan.Crop.Height}:{plan.Crop.X}:{plan.Crop.Y}," +
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
        var videoPixelFormat = GetOutputPixelFormat(plan);
        var arguments = plan.Preset.VideoCodec switch
        {
            VideoCodecFamily.H264 => new List<string>
            {
                "-c:v", "libx264", "-preset", "medium", "-crf", MapQualityAroundDefault(quality, 36, 20, 16).ToString(CultureInfo.InvariantCulture), "-pix_fmt", videoPixelFormat,
            },
            VideoCodecFamily.Vp9 =>
            [
                "-c:v", "libvpx-vp9", "-crf", MapQualityAroundDefault(quality, 50, 30, 20).ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-row-mt", "1", "-pix_fmt", videoPixelFormat,
            ],
            VideoCodecFamily.Gif => ["-c:v", "gif", "-loop", "0"],
            _ => throw new ExportPlanException($"Unsupported video codec family: {plan.Preset.VideoCodec}."),
        };
        if (plan.PreservesHdr)
        {
            if (plan.Preset.VideoCodec == VideoCodecFamily.H264)
            {
                arguments.AddRange(["-profile:v", "high10"]);
            }

            AddHdrSignalArguments(arguments, plan.OutputVideoColorInfo!);
        }
        if (plan.Preset.VideoBitRateBitsPerSecond is { } videoBitRate)
        {
            RemoveOption(arguments, "-crf");
            RemoveOption(arguments, "-b:v");
            arguments.Add("-b:v");
            arguments.Add((plan.EncodingSettings.QualityMode == ExportQualityMode.MatchSource
                    ? videoBitRate
                    : ScaleBitRate(videoBitRate, quality))
                .ToString(CultureInfo.InvariantCulture));
        }
        if (plan.Preset.FrameRate is { } frameRate)
        {
            arguments.Add("-r");
            arguments.Add($"{frameRate.Numerator}/{frameRate.Denominator}");
        }

        arguments.AddRange(CreateAudioPresetArguments(plan));

        if (plan.Preset.Container == ExportContainer.Mp4)
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }

        return arguments;
    }

    private static IEnumerable<string> CreateAudioPresetArguments(ExportPlan plan)
    {
        if (!HasAnyAudio(plan))
        {
            return [];
        }

        var baseAudioBitRate = plan.Preset.AudioBitRateBitsPerSecond ??
                               (plan.Preset.AudioCodec == AudioCodecFamily.Aac ? 192_000 : 160_000);
        var audioBitRate = (plan.EncodingSettings.QualityMode == ExportQualityMode.MatchSource
                ? baseAudioBitRate
                : ScaleBitRate(baseAudioBitRate, plan.EncodingSettings.Quality))
            .ToString(CultureInfo.InvariantCulture);
        return plan.Preset.AudioCodec switch
        {
            AudioCodecFamily.Aac => ["-c:a", "aac", "-b:a", audioBitRate],
            AudioCodecFamily.Opus => ["-c:a", "libopus", "-b:a", audioBitRate],
            _ => throw new ExportPlanException($"Unsupported audio codec family: {plan.Preset.AudioCodec}."),
        };
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

    private static void AddSequenceVideoOutputFilters(
        ICollection<string> filters,
        string input,
        ExportPlan plan)
    {
        var speed = plan.EncodingSettings.PlaybackSpeedPercent == 100
            ? string.Empty
            : $"setpts=(PTS-STARTPTS)/{FormatScalar(plan.EncodingSettings.PlaybackSpeed)},";
        if (plan.Preset.VideoCodec == VideoCodecFamily.Gif)
        {
            var maximumColors = MapQuality(plan.EncodingSettings.Quality, 32, 256);
            filters.Add(
                $"[{input}]{speed}fps={plan.EncodingSettings.GifFrameRate}," +
                "split=2[gifsource][gifpaletteinput]");
            filters.Add(
                $"[gifpaletteinput]palettegen=max_colors={maximumColors}:stats_mode=diff[gifpalette]");
            filters.Add(
                "[gifsource][gifpalette]paletteuse=dither=sierra2_4a:diff_mode=rectangle[vout]");
            return;
        }

        var colorProperties = plan.PreservesHdr
            ? $",{CreateHdrSetParameters(plan.OutputVideoColorInfo!)}"
            : string.Empty;
        filters.Add(
            $"[{input}]{speed}format={GetOutputPixelFormat(plan)}{colorProperties},setsar=1[vout]");
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
            var colorProperties = plan.PreservesHdr
                ? $",{CreateHdrSetParameters(plan.OutputVideoColorInfo!)}"
                : string.Empty;
            filters.Add(
                $"[{input}]{speed}scale={plan.OutputSize.Width}:{plan.OutputSize.Height}:flags=lanczos," +
                $"format={GetOutputPixelFormat(plan)}{colorProperties},setsar=1[vout]");
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

    private static string GetOutputPixelFormat(ExportPlan plan) =>
        plan.PreservesHdr ? "yuv420p10le" : "yuv420p";

    private static string CreateInputColorConversion(
        ExportVideoColorInfo? sourceColorInfo,
        ExportPlan plan)
    {
        if (sourceColorInfo?.IsHdr != true || plan.PreservesHdr)
        {
            return string.Empty;
        }

        return "zscale=transfer=linear:npl=100,format=gbrpf32le," +
               "zscale=primaries=bt709,tonemap=mobius:desat=0," +
               "zscale=transfer=bt709:matrix=bt709:range=tv,format=yuv420p," +
               "setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709,";
    }

    private static string CreateHdrSetParameters(ExportVideoColorInfo colorInfo) =>
        "setparams=" +
        $"range={MapColorRange(colorInfo.ColorRange)}:" +
        $"color_primaries={MapColorPrimaries(colorInfo.ColorPrimaries)}:" +
        $"color_trc={MapColorTransfer(colorInfo.ColorTransfer)}:" +
        $"colorspace={MapColorSpace(colorInfo.ColorSpace)}";

    private static void AddHdrSignalArguments(
        ICollection<string> arguments,
        ExportVideoColorInfo colorInfo)
    {
        arguments.Add("-color_range");
        arguments.Add(MapColorRange(colorInfo.ColorRange));
        arguments.Add("-color_primaries");
        arguments.Add(MapColorPrimaries(colorInfo.ColorPrimaries));
        arguments.Add("-color_trc");
        arguments.Add(MapColorTransfer(colorInfo.ColorTransfer));
        arguments.Add("-colorspace");
        arguments.Add(MapColorSpace(colorInfo.ColorSpace));
    }

    private static string MapColorRange(string? value) => value switch
    {
        "tv" or "mpeg" or "limited" => "tv",
        "pc" or "jpeg" or "full" => "pc",
        _ => throw new ExportPlanException($"Unsupported HDR color range: {value ?? "unknown"}."),
    };

    private static string MapColorPrimaries(string? value) => value switch
    {
        "bt2020" => "bt2020",
        "smpte432" => "smpte432",
        _ => throw new ExportPlanException($"Unsupported HDR color primaries: {value ?? "unknown"}."),
    };

    private static string MapColorTransfer(string? value) => value switch
    {
        "smpte2084" => "smpte2084",
        "arib-std-b67" => "arib-std-b67",
        _ => throw new ExportPlanException($"Unsupported HDR transfer: {value ?? "unknown"}."),
    };

    private static string MapColorSpace(string? value) => value switch
    {
        "bt2020nc" => "bt2020nc",
        "bt2020c" => "bt2020c",
        "ictcp" => "ictcp",
        _ => throw new ExportPlanException($"Unsupported HDR color space: {value ?? "unknown"}."),
    };

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
