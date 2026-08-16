using ClipEdit.Domain.Timeline;
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
                    ? "Fast cuts enabled; cuts snap to lossless packet-copy boundaries"
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
            ? "Selection edges, clip trims, and Split snap to indexed keyframes."
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

    private void RaiseFastCutStateChanged()
    {
        OnPropertyChanged(nameof(IsFastCutMode));
        OnPropertyChanged(nameof(IsFastCutSnappingActive));
        OnPropertyChanged(nameof(FastCutModeText));
        OnPropertyChanged(nameof(FastCutModeDetails));
    }
}
