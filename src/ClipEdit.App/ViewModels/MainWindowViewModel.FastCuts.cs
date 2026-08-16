using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<Guid, CancellationTokenSource> _keyframeIndexCancellations = [];
    private IKeyframeProbe? _keyframeProbe;
    private bool _isFastCutMode;

    public bool IsFastCutMode
    {
        get => _isFastCutMode;
        set
        {
            if (value && _keyframeProbe is null)
            {
                StatusText = "Fast cuts require ffprobe keyframe indexing.";
                return;
            }

            if (!SetProperty(ref _isFastCutMode, value))
            {
                return;
            }

            if (value)
            {
                foreach (var media in MediaItems.Where(static media => media is { IsReady: true, HasVideo: true }))
                {
                    StartKeyframeIndexing(media);
                }
            }

            RaiseFastCutStateChanged();
            StatusText = value
                ? IsFastCutSnappingActive
                    ? "Fast cuts enabled; cuts snap to indexed keyframes"
                    : "Fast cuts enabled; keyframe indexes are still loading"
                : "Exact cuts enabled; cuts may require video encoding";
        }
    }

    public bool IsFastCutSnappingActive =>
        IsFastCutMode &&
        VideoClips.Count > 0 &&
        VideoClips.Select(static clip => clip.Source).Distinct().All(static source => source.IsKeyframeIndexReady);

    public string FastCutModeText => IsFastCutMode
        ? IsFastCutSnappingActive ? "Fast" : "Fast…"
        : "Exact";

    public string FastCutModeDetails => IsFastCutMode
        ? IsFastCutSnappingActive
            ? "Selection edges, clip trims, and Split snap to indexed keyframes. Export still verifies whether a faster strategy is safe."
            : "Indexing source keyframes in the background; cuts remain exact until ready."
        : "Frame-exact cuts are preserved and re-encoded when necessary.";

    private void StartKeyframeIndexing(MediaItemViewModel media)
    {
        if (_keyframeProbe is null ||
            media is not { IsReady: true, HasVideo: true } ||
            media.IsKeyframeIndexReady ||
            media.IsKeyframeIndexLoading ||
            _keyframeIndexCancellations.ContainsKey(media.Id))
        {
            return;
        }

        var video = media.Media!.Probe.VideoStreams.First();
        var cancellation = new CancellationTokenSource();
        _keyframeIndexCancellations.Add(media.Id, cancellation);
        media.BeginKeyframeIndexing();
        RaiseFastCutStateChanged();
        _ = LoadKeyframeIndexAsync(media, video, cancellation);
    }

    private async Task LoadKeyframeIndexAsync(
        MediaItemViewModel media,
        VideoStreamInfo video,
        CancellationTokenSource cancellation)
    {
        try
        {
            var index = await _keyframeProbe!.ProbeKeyframesAsync(
                media.SourcePath,
                video.Index,
                video.StartTime ?? media.Media!.Probe.StartTime,
                video.Duration ?? media.Media!.Probe.Duration,
                cancellation.Token);
            if (!cancellation.IsCancellationRequested && MediaItems.Contains(media))
            {
                media.SetKeyframeIndex(index);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is MediaProbeException or IOException or UnauthorizedAccessException)
        {
            if (!cancellation.IsCancellationRequested && MediaItems.Contains(media))
            {
                media.SetKeyframeIndexError(exception.Message);
            }
        }
        finally
        {
            if (_keyframeIndexCancellations.Remove(media.Id, out var owned))
            {
                owned.Dispose();
            }
            RaiseFastCutStateChanged();
            RaiseExportStateChanged();
        }
    }

    private void CancelKeyframeIndexing(MediaItemViewModel media)
    {
        if (_keyframeIndexCancellations.Remove(media.Id, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void CancelAllKeyframeIndexing()
    {
        foreach (var cancellation in _keyframeIndexCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _keyframeIndexCancellations.Clear();
    }

    private MediaTime SnapTimelineCutIfEnabled(MediaTime requested, VideoClipViewModel? preferredClip = null)
    {
        if (!IsFastCutSnappingActive)
        {
            return requested;
        }

        var clips = preferredClip is null ? VideoClips : [preferredClip];
        var candidates = clips
            .SelectMany(GetTimelineCopyBoundaries)
            .Distinct()
            .ToArray();
        return candidates.Length == 0
            ? requested
            : candidates
                .OrderBy(candidate => Absolute(candidate - requested))
                .ThenBy(static candidate => candidate)
                .First();
    }

    private static IEnumerable<MediaTime> GetTimelineCopyBoundaries(VideoClipViewModel clip)
    {
        var duration = clip.Source.Edit?.SourceDuration ?? clip.Source.Media?.Probe.Duration;
        foreach (var timestamp in clip.Source.VideoKeyframes)
        {
            if (timestamp >= clip.Model.AvailableRange.Start && timestamp <= clip.Model.AvailableRange.End)
            {
                yield return clip.Model.SourceTimeToTimeline(timestamp);
            }
        }

        if (clip.Model.AvailableRange.Start == MediaTime.Zero)
        {
            yield return clip.Model.SourceTimeToTimeline(MediaTime.Zero);
        }
        if (duration is { } sourceDuration && clip.Model.AvailableRange.End == sourceDuration)
        {
            yield return clip.Model.SourceTimeToTimeline(sourceDuration);
        }
    }

    private static MediaTime Absolute(MediaTime value) => value < MediaTime.Zero ? -value : value;

    private bool TryResolveConcatStreamCopy(
        IReadOnlyList<SequenceExportSlice> slices,
        ExportPreset preset,
        out IReadOnlyList<string> reasons)
    {
        var failures = new List<string>();
        if (slices.Count == 0)
        {
            reasons = ["No video clips are selected for export."];
            return false;
        }
        if (AudioTracks.Any(track => track.IsExternal && !track.IsMuted && !track.Edit.IsEmpty))
        {
            failures.Add("External audio is not compatible with packet-copy concatenation.");
        }

        var exportRange = HasSequenceSelection
            ? NormalizedSequenceSelection()
            : new MediaRange(MediaTime.Zero, NonNegativeTimelineTime(SequenceDurationSeconds));
        var cursor = exportRange.Start;
        SegmentStreamCopyInfo? first = null;
        foreach (var slice in slices)
        {
            var clip = slice.Clip;
            var sourceDuration = clip.Source.Edit?.SourceDuration ?? clip.Source.Media?.Probe.Duration;
            if (sourceDuration is not { } duration ||
                slice.SourceRange != new MediaRange(MediaTime.Zero, duration))
            {
                failures.Add($"{clip.DisplayName} is trimmed; direct packet-copy joining currently accepts complete clips only.");
            }
            var timelineStart = clip.Model.SourceTimeToTimeline(slice.SourceRange.Start);
            if (timelineStart != cursor)
            {
                failures.Add("Packet-copy concatenation cannot contain timeline gaps.");
            }
            if (clip.PlaybackSpeedPercent != SequenceClip.DefaultPlaybackSpeedPercent)
            {
                failures.Add($"{clip.DisplayName} does not use 100% playback speed.");
            }
            if (clip.CanvasTransform != ClipCanvasTransform.Identity ||
                CanvasCrop != CropRegion.FullFrame(CanvasSize))
            {
                failures.Add($"{clip.DisplayName} has a crop or visual transform.");
            }

            var info = CreateSegmentStreamCopyInfo(
                slice,
                preset,
                requireConcatCompatibility: true);
            if (info is null)
            {
                failures.Add($"{clip.DisplayName} lacks a verified compatible stream signature.");
            }
            else if (first is null)
            {
                first = info;
            }
            else if (info.Video != first.Video || info.Audio != first.Audio)
            {
                failures.Add($"{clip.DisplayName} has different encoded stream parameters.");
            }

            cursor = timelineStart + clip.Model.SourceDurationToTimeline(slice.SourceRange.Duration);
        }

        if (cursor != exportRange.End)
        {
            failures.Add("The selected export range must end exactly at the last clip boundary.");
        }

        reasons = failures.Distinct().ToArray();
        return failures.Count == 0 && first is not null;
    }

    private SegmentStreamCopyInfo? CreateSegmentStreamCopyInfo(
        SequenceExportSlice slice,
        ExportPreset preset,
        bool requireConcatCompatibility = false)
    {
        var clip = slice.Clip;
        var probe = clip.Source.Media?.Probe;
        var video = probe?.VideoStreams.FirstOrDefault();
        if (probe is null || video is null ||
            video.RotationDegrees != 0 ||
            clip.PlaybackSpeedPercent != SequenceClip.DefaultPlaybackSpeedPercent ||
            clip.CanvasTransform != ClipCanvasTransform.Identity ||
            CanvasSize != video.EncodedSize ||
            CanvasCrop != CropRegion.FullFrame(CanvasSize) ||
            !SourceVideoCodecMatches(video.CodecName, preset.VideoCodec) ||
            CreateVideoStreamCopySignature(video) is not { } videoSignature)
        {
            return null;
        }

        var sourceDuration = clip.Source.Edit?.SourceDuration ?? probe.Duration;
        if (sourceDuration is not { } completeDuration)
        {
            return null;
        }
        var startPoint = clip.Source.VideoKeyframePoints.FirstOrDefault(point =>
            point.PresentationTimestamp == slice.SourceRange.Start);
        var endPoint = clip.Source.VideoKeyframePoints.FirstOrDefault(point =>
            point.PresentationTimestamp == slice.SourceRange.End);
        var startsAtSourceStart = slice.SourceRange.Start == MediaTime.Zero;
        var endsAtSourceEnd = slice.SourceRange.End == completeDuration;
        var startsOnBoundary = startsAtSourceStart || startPoint?.DecodeTimestamp is not null;
        var endsOnBoundary = endsAtSourceEnd || endPoint?.DecodeTimestamp is not null;
        if (!startsOnBoundary || !endsOnBoundary)
        {
            return null;
        }

        var embedded = AudioTracks
            .Where(track =>
                preset.SupportsAudio &&
                !track.IsExternal &&
                !track.IsMuted &&
                track.EmbeddedLaneIndex is { } laneIndex &&
                clip.IncludesAudioLane(laneIndex) &&
                track.TryGetEmbeddedStreamIndex(clip.SourcePath, out _))
            .ToArray();
        if (requireConcatCompatibility && embedded.Length > 1)
        {
            return null;
        }

        var selectedStreamCount = 1 + embedded.Length;
        if (requireConcatCompatibility && probe.Streams.Length != selectedStreamCount)
        {
            return null;
        }

        AudioStreamCopySignature? audioSignature = null;
        if (embedded.Length == 1)
        {
            var track = embedded[0];
            var laneIndex = track.EmbeddedLaneIndex!.Value;
            var streamIndex = -1;
            var canCopyAudio =
                Math.Abs(CombineAudioGain(track.GainDb, clip.GetAudioLaneGainDb(laneIndex))) < 0.000_001 &&
                track.CreateEditForClip(clip).IsUnedited &&
                track.TryGetEmbeddedStreamIndex(clip.SourcePath, out streamIndex);
            var audio = canCopyAudio
                ? probe.AudioStreams.FirstOrDefault(stream => stream.Index == streamIndex)
                : null;
            if (audio is not null &&
                SourceAudioCodecMatches(audio.CodecName, preset.AudioCodec) &&
                CreateAudioStreamCopySignature(audio) is { } signature)
            {
                audioSignature = signature;
            }
            else if (requireConcatCompatibility)
            {
                return null;
            }
        }

        return new SegmentStreamCopyInfo(
            videoSignature,
            audioSignature,
            startsOnBoundary,
            endsOnBoundary,
            startPoint?.DecodeTimestamp,
            endPoint?.DecodeTimestamp);
    }

    private bool CanCopyTrimmedVideoPackets(
        SequenceExportSlice slice,
        ExportPreset preset,
        SegmentStreamCopyInfo? streamCopyInfo)
    {
        var probe = slice.Clip.Source.Media?.Probe;
        var video = probe?.VideoStreams.FirstOrDefault();
        var sourceDuration = slice.Clip.Source.Edit?.SourceDuration ?? probe?.Duration;
        if (video is null || sourceDuration is not { } duration ||
            !SourceVideoCodecMatches(video.CodecName, VideoCodecFamily.H264) ||
            preset.VideoCodec != VideoCodecFamily.H264 ||
            streamCopyInfo is not
            {
                StartsOnKeyframeOrAtSourceStart: true,
                EndsOnKeyframeOrAtSourceEnd: true,
            })
        {
            return false;
        }

        var range = slice.SourceRange;
        if (range == new MediaRange(MediaTime.Zero, duration))
        {
            return false;
        }
        if (range.Start > MediaTime.Zero && streamCopyInfo.StartDecodeTimestamp is null)
        {
            return false;
        }
        return range.End == duration ||
               streamCopyInfo.EndDecodeTimestamp is { } endDts && endDts > range.Start;
    }

    private static VideoStreamCopySignature? CreateVideoStreamCopySignature(VideoStreamInfo video)
    {
        if (string.IsNullOrWhiteSpace(video.CodecTag) ||
            string.IsNullOrWhiteSpace(video.CodecExtradataHash) ||
            string.IsNullOrWhiteSpace(video.PixelFormat) ||
            video.TimeBase is not { } timeBase ||
            video.AverageFrameRate is not { IsZero: false } frameRate)
        {
            return null;
        }

        return new VideoStreamCopySignature(
            video.CodecName,
            video.CodecTag,
            video.CodecExtradataHash,
            video.EncodedSize,
            timeBase,
            frameRate,
            video.PixelFormat,
            video.Profile,
            video.CodecLevel,
            video.SampleAspectRatio,
            video.ColorRange,
            video.ColorSpace,
            video.ColorTransfer,
            video.ColorPrimaries,
            video.FieldOrder);
    }

    private static AudioStreamCopySignature? CreateAudioStreamCopySignature(AudioStreamInfo audio)
    {
        if (string.IsNullOrWhiteSpace(audio.CodecTag) ||
            string.IsNullOrWhiteSpace(audio.CodecExtradataHash) ||
            string.IsNullOrWhiteSpace(audio.ChannelLayout) ||
            string.IsNullOrWhiteSpace(audio.SampleFormat) ||
            audio.TimeBase is not { } timeBase ||
            audio.SampleRate is not { } sampleRate ||
            audio.ChannelCount is not { } channelCount)
        {
            return null;
        }

        return new AudioStreamCopySignature(
            audio.CodecName,
            audio.CodecTag,
            audio.CodecExtradataHash,
            timeBase,
            sampleRate,
            channelCount,
            audio.ChannelLayout,
            audio.SampleFormat,
            audio.Profile);
    }

    private void RaiseFastCutStateChanged()
    {
        OnPropertyChanged(nameof(IsFastCutMode));
        OnPropertyChanged(nameof(IsFastCutSnappingActive));
        OnPropertyChanged(nameof(FastCutModeText));
        OnPropertyChanged(nameof(FastCutModeDetails));
    }
}
