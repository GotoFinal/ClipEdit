using System.Collections.Immutable;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Export;

public enum ExportStrategy
{
    ExactTranscode,
    StreamCopy,
    EditListStreamCopy,
    VideoStreamCopy,
    ConcatStreamCopy,
    BoundaryGop,
}

/// <summary>
/// A validated, immutable rendering decision for one source video.
/// </summary>
public sealed record ExportPlan
{
    private readonly PixelSize _outputSize;
    private readonly MediaTime _expectedDuration;
    private readonly MediaTime _timelineDuration;
    private readonly ImmutableArray<MediaTime> _videoSegmentTimelineStarts;

    public ExportPlan(
        string sourcePath,
        string destinationPath,
        int videoStreamIndex,
        int? audioStreamIndex,
        CropRegion crop,
        ImmutableArray<MediaRange> sourceRanges,
        ExportPreset preset,
        bool replaceExistingDestination = false,
        ImmutableArray<ExportAudioTrackPlan> audioTracks = default,
        ExportEncodingSettings? encodingSettings = null,
        ExportVideoColorInfo? sourceVideoColorInfo = null,
        ExportStrategy strategy = ExportStrategy.ExactTranscode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (audioStreamIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioStreamIndex));
        }

        ArgumentNullException.ThrowIfNull(preset);
        if (preset.ParameterMode != ExportParameterMode.Fixed)
        {
            throw new ExportPlanException("A source-matching export intent must be resolved before planning.");
        }
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("The export source path must be absolute.", nameof(sourcePath));
        }

        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The export destination path must be absolute.", nameof(destinationPath));
        }

        if (sourceRanges.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one non-empty source range is required.", nameof(sourceRanges));
        }

        ValidateRanges(sourceRanges);
        EncodingSettings = encodingSettings ?? ExportEncodingSettings.Default;
        _outputSize = EncodingSettings.CalculateOutputSize(
            crop.ExportSize,
            preset.RequiresEvenDimensions);
        if (preset.RequiresEvenDimensions &&
            (((_outputSize.Width & 1) != 0) || ((_outputSize.Height & 1) != 0)))
        {
            throw new ExportPlanException(
                $"{preset.DisplayName} requires even dimensions, but the output is {_outputSize.Width} × {_outputSize.Height}.");
        }

        SourcePath = Path.GetFullPath(sourcePath);
        DestinationPath = Path.GetFullPath(destinationPath);
        VideoStreamIndex = videoStreamIndex;
        AudioTracks = audioTracks.IsDefault
            ? audioStreamIndex is null
                ? []
                : [new ExportAudioTrackPlan(audioStreamIndex.Value, 0)]
            : audioTracks;
        if (AudioTracks.Any(track => track is null) || HasDuplicateAudioTracks(AudioTracks))
        {
            throw new ArgumentException(
                "Export audio tracks must be non-null and use distinct source/stream identities.",
                nameof(audioTracks));
        }
        Crop = crop;
        SourceRanges = sourceRanges;
        Preset = preset;
        ReplaceExistingDestination = replaceExistingDestination;
        VideoSegments = [];
        SourceVideoColorInfo = sourceVideoColorInfo;
        OutputVideoColorInfo = ResolveSingleSourceOutputColorInfo(preset, sourceVideoColorInfo);
        SequenceTimelineStart = MediaTime.Zero;
        _timelineDuration = SourceRanges.Aggregate(MediaTime.Zero, static (total, range) => total + range.Duration);
        _expectedDuration = EncodingSettings.ApplyPlaybackSpeed(_timelineDuration);
        _videoSegmentTimelineStarts = [];
        if (strategy != ExportStrategy.ExactTranscode)
        {
            throw new ExportPlanException("Packet-copy export is only supported for a validated sequence clip.");
        }
        Strategy = strategy;
    }

    public ExportPlan(
        ImmutableArray<ExportVideoSegmentPlan> videoSegments,
        PixelSize outputSize,
        string destinationPath,
        ExportPreset preset,
        bool replaceExistingDestination = false,
        ImmutableArray<ExportAudioTrackPlan> externalAudioTracks = default,
        MediaTime sequenceTimelineStart = default,
        MediaTime? sequenceDuration = null,
        ExportEncodingSettings? encodingSettings = null,
        ExportStrategy strategy = ExportStrategy.ExactTranscode)
    {
        if (videoSegments.IsDefaultOrEmpty || videoSegments.Any(segment => segment is null))
        {
            throw new ArgumentException("At least one video segment is required.", nameof(videoSegments));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.ParameterMode != ExportParameterMode.Fixed)
        {
            throw new ExportPlanException("A source-matching export intent must be resolved before planning.");
        }
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The export destination path must be absolute.", nameof(destinationPath));
        }

        EncodingSettings = encodingSettings ?? ExportEncodingSettings.Default;
        var scaledOutputSize = EncodingSettings.CalculateOutputSize(
            outputSize,
            preset.RequiresEvenDimensions);
        if (preset.RequiresEvenDimensions &&
            (((scaledOutputSize.Width & 1) != 0) || ((scaledOutputSize.Height & 1) != 0)))
        {
            throw new ExportPlanException(
                $"{preset.DisplayName} requires even dimensions, but the output is {scaledOutputSize.Width} × {scaledOutputSize.Height}.");
        }

        var externalTracks = externalAudioTracks.IsDefault ? [] : externalAudioTracks;
        if (sequenceTimelineStart < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceTimelineStart));
        }
        if (externalTracks.Any(track => track is null || !track.IsExternal) ||
            HasDuplicateAudioTracks(externalTracks))
        {
            throw new ArgumentException(
                "Sequence-level audio tracks must be distinct external sources.",
                nameof(externalAudioTracks));
        }

        var starts = ImmutableArray.CreateBuilder<MediaTime>(videoSegments.Length);
        var cursor = sequenceTimelineStart;
        foreach (var segment in videoSegments)
        {
            var start = segment.TimelineStart ?? cursor;
            if (start < cursor)
            {
                throw new ArgumentException(
                    "Sequence video segments must be ordered and cannot overlap.",
                    nameof(videoSegments));
            }

            starts.Add(start);
            cursor = start + segment.TimelineDuration;
        }

        var resolvedDuration = sequenceDuration ?? (cursor - sequenceTimelineStart);
        if (resolvedDuration <= MediaTime.Zero ||
            cursor > sequenceTimelineStart + resolvedDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceDuration),
                "The sequence duration must contain every placed video segment.");
        }

        _videoSegmentTimelineStarts = starts.MoveToImmutable();
        _timelineDuration = resolvedDuration;
        _expectedDuration = EncodingSettings.ApplyPlaybackSpeed(resolvedDuration);
        VideoSegments = videoSegments;
        SourceVideoColorInfo = videoSegments[0].VideoColorInfo;
        OutputVideoColorInfo = ResolveSequenceOutputColorInfo(preset, videoSegments);
        _outputSize = scaledOutputSize;
        DestinationPath = Path.GetFullPath(destinationPath);
        Preset = preset;
        ReplaceExistingDestination = replaceExistingDestination;
        AudioTracks = externalTracks;
        SourcePath = videoSegments[0].SourcePath;
        VideoStreamIndex = videoSegments[0].VideoStreamIndex;
        Crop = videoSegments[0].Crop;
        SourceRanges = videoSegments.Select(segment => segment.SourceRange).ToImmutableArray();
        SequenceTimelineStart = sequenceTimelineStart;
        ValidateSequenceStrategy(strategy, videoSegments, externalTracks, resolvedDuration);
        Strategy = strategy;
    }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public int VideoStreamIndex { get; }

    public ImmutableArray<ExportAudioTrackPlan> AudioTracks { get; }

    public ImmutableArray<ExportVideoSegmentPlan> VideoSegments { get; }

    public ExportVideoColorInfo? SourceVideoColorInfo { get; }

    public ExportVideoColorInfo? OutputVideoColorInfo { get; }

    public bool PreservesHdr => OutputVideoColorInfo?.IsHdr == true;

    public bool IsSequence => !VideoSegments.IsDefaultOrEmpty;

    public MediaTime SequenceTimelineStart { get; }

    public int? AudioStreamIndex => AudioTracks.Length == 1 ? AudioTracks[0].StreamIndex : null;

    public CropRegion Crop { get; }

    public ImmutableArray<MediaRange> SourceRanges { get; }
    public MediaTime GetVideoSegmentTimelineStart(int index)
    {
        if (!IsSequence || index < 0 || index >= _videoSegmentTimelineStarts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return _videoSegmentTimelineStarts[index];
    }

    public ExportPreset Preset { get; }

    public ExportEncodingSettings EncodingSettings { get; }

    public bool ReplaceExistingDestination { get; }

    public ExportStrategy Strategy { get; }

    public PixelSize OutputSize => _outputSize;

    public MediaTime ExpectedDuration => _expectedDuration;

    public MediaTime TimelineDuration => _timelineDuration;

    private static void ValidateRanges(ImmutableArray<MediaRange> ranges)
    {
        var previousEnd = MediaTime.Zero;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.IsEmpty || range.Start < MediaTime.Zero)
            {
                throw new ArgumentException("Export ranges must be non-empty and non-negative.", nameof(ranges));
            }

            if (index > 0 && range.Start < previousEnd)
            {
                throw new ArgumentException("Export ranges must be ordered and non-overlapping.", nameof(ranges));
            }

            previousEnd = range.End;
        }
    }

    private static bool HasDuplicateAudioTracks(ImmutableArray<ExportAudioTrackPlan> tracks)
    {
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var left = 0; left < tracks.Length; left++)
        {
            for (var right = left + 1; right < tracks.Length; right++)
            {
                if (tracks[left].StreamIndex == tracks[right].StreamIndex &&
                    string.Equals(
                        tracks[left].ExternalSourcePath,
                        tracks[right].ExternalSourcePath,
                        pathComparison))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void ValidateSequenceStrategy(
        ExportStrategy strategy,
        ImmutableArray<ExportVideoSegmentPlan> segments,
        ImmutableArray<ExportAudioTrackPlan> externalTracks,
        MediaTime resolvedDuration)
    {
        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }
        if (strategy == ExportStrategy.ConcatStreamCopy)
        {
            ValidateConcatStreamCopy(segments, externalTracks, resolvedDuration);
            return;
        }
        if (strategy == ExportStrategy.BoundaryGop)
        {
            ValidateBoundaryGop(segments, resolvedDuration);
            return;
        }
        if (strategy == ExportStrategy.EditListStreamCopy)
        {
            ValidateEditListStreamCopy(segments, externalTracks, resolvedDuration);
            return;
        }
        if (strategy is not (ExportStrategy.StreamCopy or ExportStrategy.VideoStreamCopy))
        {
            return;
        }

        var segment = segments.Length == 1 ? segments[0] : null;
        var hasUnchangedAudio = strategy == ExportStrategy.VideoStreamCopy ||
                                (segment?.AudioTracks.Length switch
                                {
                                    0 => true,
                                    1 => Math.Abs(segment.AudioTracks[0].GainDb) < 0.000_001 &&
                                         segment.AudioTracks[0].AudioEdit is { IsUnedited: true },
                                    _ => false,
                                });
        var isVerifiedVideoTrim = strategy == ExportStrategy.VideoStreamCopy &&
                                  segment is
                                  {
                                      StreamCopyInfo.StartsOnKeyframeOrAtSourceStart: true,
                                      StreamCopyInfo.EndsOnKeyframeOrAtSourceEnd: true,
                                  } &&
                                  (segment.SourceRange.Start == MediaTime.Zero ||
                                   segment.StreamCopyInfo.StartDecodeTimestamp is not null) &&
                                  (segment.IsCompleteSource ||
                                   segment.StreamCopyInfo.EndDecodeTimestamp is { } endDts &&
                                   endDts > segment.SourceRange.Start);
        if (segment is null ||
            (strategy == ExportStrategy.StreamCopy && !externalTracks.IsEmpty) ||
            (!segment.IsCompleteSource && !isVerifiedVideoTrim) ||
            segment.PlaybackSpeedPercent != SequenceClip.DefaultPlaybackSpeedPercent ||
            segment.TimelineStart != SequenceTimelineStart ||
            segment.TimelineDuration != resolvedDuration ||
            segment.CanvasTransform != ClipCanvasTransform.Identity ||
            segment.CanvasCrop != CropRegion.FullFrame(segment.CanvasSize) ||
            EncodingSettings.ScalePercent != ExportEncodingSettings.DefaultScalePercent ||
            EncodingSettings.PlaybackSpeedPercent != ExportEncodingSettings.DefaultPlaybackSpeedPercent ||
            !hasUnchangedAudio)
        {
            throw new ExportPlanException(
                strategy == ExportStrategy.StreamCopy
                    ? "Packet-copy export requires one complete, untransformed, untrimmed source clip with unchanged audio."
                    : "Video-copy export requires one complete clip or a verified keyframe-aligned trim without visual transforms.");
        }
    }

    private void ValidateEditListStreamCopy(
        ImmutableArray<ExportVideoSegmentPlan> segments,
        ImmutableArray<ExportAudioTrackPlan> externalTracks,
        MediaTime resolvedDuration)
    {
        var segment = segments.Length == 1 ? segments[0] : null;
        var hasUnchangedAudio = segment?.AudioTracks.Length switch
        {
            0 => true,
            1 => Math.Abs(segment.AudioTracks[0].GainDb) < 0.000_001 &&
                 segment.AudioTracks[0].AudioEdit is { IsUnedited: true },
            _ => false,
        };
        if (segment is null ||
            Preset.Container != ExportContainer.Mp4 ||
            Preset.VideoCodec != VideoCodecFamily.H264 ||
            segment.IsCompleteSource ||
            !externalTracks.IsEmpty ||
            !hasUnchangedAudio ||
            segment.PlaybackSpeedPercent != SequenceClip.DefaultPlaybackSpeedPercent ||
            segment.TimelineStart != SequenceTimelineStart ||
            segment.TimelineDuration != resolvedDuration ||
            segment.CanvasTransform != ClipCanvasTransform.Identity ||
            segment.CanvasCrop != CropRegion.FullFrame(segment.CanvasSize) ||
            EncodingSettings.ScalePercent != ExportEncodingSettings.DefaultScalePercent ||
            EncodingSettings.PlaybackSpeedPercent != ExportEncodingSettings.DefaultPlaybackSpeedPercent)
        {
            throw new ExportPlanException(
                "MP4 edit-list packet trim requires one trimmed H.264 clip with unchanged video, audio, size, and speed.");
        }
    }

    private void ValidateBoundaryGop(
        ImmutableArray<ExportVideoSegmentPlan> segments,
        MediaTime resolvedDuration)
    {
        var segment = segments.Length == 1 ? segments[0] : null;
        var codecMatches = segment?.BoundaryGopInfo?.Video.CodecName.ToLowerInvariant() switch
        {
            "h264" => Preset.VideoCodec == VideoCodecFamily.H264 &&
                      Preset.Container is ExportContainer.Mp4 or ExportContainer.Matroska,
            "vp9" => Preset.VideoCodec == VideoCodecFamily.Vp9 &&
                     Preset.Container is ExportContainer.WebM or ExportContainer.Matroska,
            "av1" => Preset.VideoCodec == VideoCodecFamily.Av1 &&
                     Preset.Container is ExportContainer.Mp4 or ExportContainer.WebM or ExportContainer.Matroska,
            _ => false,
        };
        if (segment is null ||
            segment.BoundaryGopInfo is null ||
            !codecMatches ||
            segment.IsCompleteSource ||
            segment.PlaybackSpeedPercent != SequenceClip.DefaultPlaybackSpeedPercent ||
            segment.TimelineStart != SequenceTimelineStart ||
            segment.TimelineDuration != resolvedDuration ||
            segment.CanvasTransform != ClipCanvasTransform.Identity ||
            segment.CanvasCrop != CropRegion.FullFrame(segment.CanvasSize) ||
            segment.SourceSize != segment.CanvasSize ||
            EncodingSettings.ScalePercent != ExportEncodingSettings.DefaultScalePercent ||
            EncodingSettings.PlaybackSpeedPercent != ExportEncodingSettings.DefaultPlaybackSpeedPercent)
        {
            throw new ExportPlanException(
                "Boundary-GOP rendering requires one CFR H.264, VP9, or AV1 trim with source-matching output and no visual transforms.");
        }
    }

    private void ValidateConcatStreamCopy(
        ImmutableArray<ExportVideoSegmentPlan> segments,
        ImmutableArray<ExportAudioTrackPlan> externalTracks,
        MediaTime resolvedDuration)
    {
        var cursor = SequenceTimelineStart;
        if (segments[0].StreamCopyInfo is not { Video: not null } firstInfo)
        {
            throw new ExportPlanException(
                "Packet-copy concatenation requires verified encoded stream signatures.");
        }
        var hasAudio = segments[0].AudioTracks.Length == 1;
        if (hasAudio && firstInfo.Audio is null)
        {
            throw new ExportPlanException(
                "Packet-copy concatenation requires a verified unchanged audio stream signature.");
        }
        var videoStreamIndex = segments[0].VideoStreamIndex;
        var audioStreamIndex = hasAudio ? segments[0].AudioTracks[0].StreamIndex : (int?)null;
        var compatible = externalTracks.IsEmpty &&
                         EncodingSettings.ScalePercent == ExportEncodingSettings.DefaultScalePercent &&
                         EncodingSettings.PlaybackSpeedPercent == ExportEncodingSettings.DefaultPlaybackSpeedPercent;
        foreach (var segment in segments)
        {
            var info = segment.StreamCopyInfo;
            compatible &= segment.TimelineStart == cursor &&
                          segment.IsCompleteSource &&
                          segment.VideoStreamIndex == videoStreamIndex &&
                          segment.PlaybackSpeedPercent == SequenceClip.DefaultPlaybackSpeedPercent &&
                          segment.CanvasTransform == ClipCanvasTransform.Identity &&
                          segment.CanvasCrop == CropRegion.FullFrame(segment.CanvasSize) &&
                          info is not null &&
                          info.Video is not null &&
                          info.Video == firstInfo.Video &&
                          info.Audio == firstInfo.Audio &&
                          info.StartsOnKeyframeOrAtSourceStart &&
                          info.EndsOnKeyframeOrAtSourceEnd &&
                          segment.AudioTracks.Length == (hasAudio ? 1 : 0) &&
                          (!hasAudio || segment.AudioTracks[0].StreamIndex == audioStreamIndex) &&
                          segment.AudioTracks.All(track =>
                              Math.Abs(track.GainDb) < 0.000_001 &&
                              track.AudioEdit is { IsUnedited: true });
            cursor += segment.TimelineDuration;
        }

        if (!compatible || cursor != SequenceTimelineStart + resolvedDuration)
        {
            throw new ExportPlanException(
                "Packet-copy concatenation requires contiguous complete clips with identical encoded stream signatures and unchanged audio.");
        }
    }

    private static ExportVideoColorInfo? ResolveSingleSourceOutputColorInfo(
        ExportPreset preset,
        ExportVideoColorInfo? colorInfo) =>
        preset.VideoCodec != VideoCodecFamily.Gif && colorInfo?.CanPreserveHdr == true
            ? colorInfo
            : null;

    private static ExportVideoColorInfo? ResolveSequenceOutputColorInfo(
        ExportPreset preset,
        ImmutableArray<ExportVideoSegmentPlan> segments)
    {
        if (preset.VideoCodec == VideoCodecFamily.Gif || segments[0].VideoColorInfo is not { CanPreserveHdr: true } first)
        {
            return null;
        }

        return segments.All(segment => first.IsCompatibleHdr(segment.VideoColorInfo))
            ? first
            : null;
    }
}

public sealed record ExportVideoSegmentPlan
{
    public ExportVideoSegmentPlan(
        string sourcePath,
        int videoStreamIndex,
        MediaRange sourceRange,
        CropRegion crop,
        ImmutableArray<ExportAudioTrackPlan> audioTracks = default,
        ExportVideoColorInfo? videoColorInfo = null)
        : this(
            sourcePath,
            videoStreamIndex,
            sourceRange,
            crop.SourceSize,
            crop,
            ClipCanvasTransform.Identity,
            audioTracks,
            videoColorInfo: videoColorInfo,
            sourceSize: crop.SourceSize)
    {
        UsesCanvasTransform = false;
    }

    public ExportVideoSegmentPlan(
        string sourcePath,
        int videoStreamIndex,
        MediaRange sourceRange,
        PixelSize canvasSize,
        CropRegion canvasCrop,
        ClipCanvasTransform canvasTransform,
        ImmutableArray<ExportAudioTrackPlan> audioTracks = default,
        MediaTime? timelineStart = null,
        int playbackSpeedPercent = SequenceClip.DefaultPlaybackSpeedPercent,
        ExportVideoColorInfo? videoColorInfo = null,
        bool isCompleteSource = false,
        PixelSize? sourceSize = null,
        SegmentStreamCopyInfo? streamCopyInfo = null,
        BoundaryGopRenderInfo? boundaryGopInfo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (!Path.IsPathFullyQualified(sourcePath) || sourceRange.IsEmpty || sourceRange.Start < MediaTime.Zero)
        {
            throw new ArgumentException("A video segment source and range must be valid.");
        }

        if (canvasCrop.SourceSize != canvasSize)
        {
            throw new ArgumentException("The canvas crop must use the segment canvas size.", nameof(canvasCrop));
        }
        if (playbackSpeedPercent is < SequenceClip.MinimumPlaybackSpeedPercent or
            > SequenceClip.MaximumPlaybackSpeedPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(playbackSpeedPercent));
        }

        var embeddedTracks = audioTracks.IsDefault ? [] : audioTracks;
        if (embeddedTracks.Any(track => track is null || track.IsExternal) ||
            embeddedTracks.Select(track => track.StreamIndex).Distinct().Count() != embeddedTracks.Length)
        {
            throw new ArgumentException(
                "Segment audio tracks must be distinct embedded streams.",
                nameof(audioTracks));
        }

        SourcePath = Path.GetFullPath(sourcePath);
        VideoStreamIndex = videoStreamIndex;
        SourceRange = sourceRange;
        SourceSize = sourceSize;
        CanvasSize = canvasSize;
        if (timelineStart < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineStart));
        }

        CanvasCrop = canvasCrop;
        CanvasTransform = canvasTransform;
        AudioTracks = embeddedTracks;
        TimelineStart = timelineStart;
        PlaybackSpeedPercent = playbackSpeedPercent;
        VideoColorInfo = videoColorInfo;
        IsCompleteSource = isCompleteSource;
        StreamCopyInfo = streamCopyInfo;
        BoundaryGopInfo = boundaryGopInfo;
        UsesCanvasTransform = true;
    }

    public string SourcePath { get; }

    public int VideoStreamIndex { get; }

    public MediaRange SourceRange { get; }

    /// <summary>
    /// Rotation-corrected source raster presented to the FFmpeg filter graph,
    /// when known. This allows export lowering to prove when an axis-aligned
    /// transform can bypass canvas compositing without changing its result.
    /// </summary>
    public PixelSize? SourceSize { get; }

    public PixelSize CanvasSize { get; }

    public CropRegion CanvasCrop { get; }

    public MediaTime? TimelineStart { get; }

    public int PlaybackSpeedPercent { get; }

    public double PlaybackSpeed => PlaybackSpeedPercent / 100d;

    public MediaTime TimelineDuration => SourceRange.Duration * 100 / PlaybackSpeedPercent;

    public ClipCanvasTransform CanvasTransform { get; }

    public ExportVideoColorInfo? VideoColorInfo { get; }

    public bool UsesCanvasTransform { get; private init; }

    public bool IsCompleteSource { get; }

    public SegmentStreamCopyInfo? StreamCopyInfo { get; }

    public BoundaryGopRenderInfo? BoundaryGopInfo { get; }

    public CropRegion Crop => CanvasCrop;

    public ImmutableArray<ExportAudioTrackPlan> AudioTracks { get; }
}

public sealed record ExportAudioTrackPlan
{
    public ExportAudioTrackPlan(int streamIndex, double gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(streamIndex);
        if (!double.IsFinite(gainDb) || gainDb is < -60 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb));
        }

        StreamIndex = streamIndex;
        GainDb = gainDb;
    }

    public ExportAudioTrackPlan(int streamIndex, double gainDb, SourceEdit audioEdit)
        : this(streamIndex, gainDb)
    {
        AudioEdit = audioEdit ?? throw new ArgumentNullException(nameof(audioEdit));
    }

    public ExportAudioTrackPlan(string externalSourcePath, int streamIndex, double gainDb)
        : this(externalSourcePath, streamIndex, gainDb, MediaTime.Zero)
    {
    }

    public ExportAudioTrackPlan(
        string externalSourcePath,
        int streamIndex,
        double gainDb,
        MediaTime timelineOffset)
        : this(streamIndex, gainDb)
    {
        if (timelineOffset < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timelineOffset),
                "An external audio timeline offset cannot be negative.");
        }

        ExternalSourcePath = ValidateExternalSourcePath(externalSourcePath);
        TimelineOffset = timelineOffset;
    }

    public ExportAudioTrackPlan(
        string externalSourcePath,
        int streamIndex,
        double gainDb,
        MediaTime timelineOffset,
        SourceEdit audioEdit)
        : this(externalSourcePath, streamIndex, gainDb, timelineOffset)
    {
        AudioEdit = audioEdit ?? throw new ArgumentNullException(nameof(audioEdit));
    }

    public string? ExternalSourcePath { get; }

    public bool IsExternal => ExternalSourcePath is not null;

    public MediaTime TimelineOffset { get; }

    public SourceEdit? AudioEdit { get; }

    public int StreamIndex { get; }

    public double GainDb { get; }

    private static string ValidateExternalSourcePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("An external export audio path must be absolute.", nameof(sourcePath));
        }

        return Path.GetFullPath(sourcePath);
    }
}

public sealed class ExportPlanException(string message) : Exception(message);
