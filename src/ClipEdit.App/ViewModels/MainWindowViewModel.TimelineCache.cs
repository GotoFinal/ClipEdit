using Avalonia.Media.Imaging;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Frames;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void StartCachedSequenceTimelineAnalysis(bool debounce)
    {
        _sequenceTimelineAnalysisCancellation?.Cancel();
        _sequenceTimelineAnalysisCancellation = null;
        if (_frameDecoder is null || VideoClips.Count == 0 || SequenceDurationSeconds <= 0)
        {
            return;
        }

        var clips = VideoClips.ToArray();
        var viewportStart = SequenceTimelineViewportStart;
        var viewportEnd = SequenceTimelineViewportEnd;
        var bucketDuration = TimelineFrameCache.ChooseBucketDuration(
            Math.Max(0.000001, viewportEnd - viewportStart) / SequenceViewportThumbnailCount);
        var frameRequests = CreateSequenceFrameRequests(
            clips,
            viewportStart,
            viewportEnd,
            SequenceViewportThumbnailCount,
            bucketDuration);

        // Zoom and pan can repaint from warm encoded images synchronously. Missing
        // cells retain the previous filmstrip until the background fill completes.
        ApplyCachedSequenceFrames(clips, frameRequests, requireComplete: false);

        var request = new CancellationTokenSource();
        _sequenceTimelineAnalysisCancellation = request;
        _ = RefreshCachedSequenceTimelineAsync(
            clips,
            frameRequests,
            viewportStart,
            viewportEnd,
            bucketDuration,
            request,
            debounce);
    }

    private async Task RefreshCachedSequenceTimelineAsync(
        IReadOnlyList<VideoClipViewModel> clips,
        IReadOnlyList<SequenceFrameRequest> frameRequests,
        double viewportStart,
        double viewportEnd,
        double bucketDuration,
        CancellationTokenSource request,
        bool debounce)
    {
        var token = request.Token;
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(55), token);
            }

            foreach (var clip in clips.Where(clip => frameRequests.Any(item => ReferenceEquals(item.Clip, clip))))
            {
                clip.IsTimelineLoading = true;
            }

            await FillTimelineCacheAsync(frameRequests, token);
            if (!ReferenceEquals(_sequenceTimelineAnalysisCancellation, request))
            {
                return;
            }

            ApplyCachedSequenceFrames(clips, frameRequests, requireComplete: true);

            // After a short idle period, prepare the next finer zoom level. This is
            // still viewport-bounded and is canceled immediately by any edit/pan.
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);
            var finerBucketDuration = Math.Max(
                TimelineFrameCache.MinimumBucketDurationSeconds,
                bucketDuration / 2);
            if (finerBucketDuration < bucketDuration)
            {
                var prefetchRequests = CreateSequenceFrameRequests(
                    clips,
                    viewportStart,
                    viewportEnd,
                    SequenceViewportThumbnailCount * 2,
                    finerBucketDuration);
                await FillTimelineCacheAsync(prefetchRequests, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer sequence or viewport superseded this cache request.
        }
        catch (Exception exception) when (exception is FrameDecodeException or IOException)
        {
            if (ReferenceEquals(_sequenceTimelineAnalysisCancellation, request))
            {
                StatusText = $"Timeline thumbnails are unavailable: {exception.Message}";
            }
        }
        finally
        {
            foreach (var clip in clips)
            {
                if (VideoClips.Contains(clip))
                {
                    clip.IsTimelineLoading = false;
                }
            }

            if (ReferenceEquals(_sequenceTimelineAnalysisCancellation, request))
            {
                _sequenceTimelineAnalysisCancellation = null;
            }

            request.Dispose();
        }
    }

    private async Task FillTimelineCacheAsync(
        IReadOnlyList<SequenceFrameRequest> frameRequests,
        CancellationToken cancellationToken)
    {
        var missing = frameRequests
            .GroupBy(item => item.CacheKey)
            .Select(group => group.First())
            .Where(item => !_timelineFrameCache.TryGet(item.CacheKey, out _))
            .ToArray();
        await Task.WhenAll(missing.Select(item => DecodeTimelineFrameAsync(item, cancellationToken)));
    }

    private async Task DecodeTimelineFrameAsync(
        SequenceFrameRequest request,
        CancellationToken cancellationToken)
    {
        if (_timelineFrameCache.TryGet(request.CacheKey, out _))
        {
            return;
        }

        await _analysisSlots.WaitAsync(cancellationToken);
        try
        {
            if (_timelineFrameCache.TryGet(request.CacheKey, out _))
            {
                return;
            }

            var decoded = await _frameDecoder!.DecodeAsync(
                request.Clip.SourcePath,
                request.VideoStreamIndex,
                ToMediaTime(request.DecodeTimestamp),
                request.CacheKey.MaximumSize,
                cancellationToken);
            _timelineFrameCache.Set(request.CacheKey, decoded.EncodedImage);
        }
        finally
        {
            _analysisSlots.Release();
        }
    }

    private IReadOnlyList<SequenceFrameRequest> CreateSequenceFrameRequests(
        IReadOnlyList<VideoClipViewModel> clips,
        double viewportStart,
        double viewportEnd,
        int viewportThumbnailCount,
        double bucketDuration)
    {
        var requests = new List<SequenceFrameRequest>(viewportThumbnailCount);
        var viewportDuration = Math.Max(0.000001, viewportEnd - viewportStart);
        foreach (var clip in clips)
        {
            var visibleStart = Math.Max(viewportStart, clip.TimelineStartSeconds);
            var visibleEnd = Math.Min(viewportEnd, clip.TimelineEndSeconds);
            if (visibleEnd <= visibleStart ||
                clip.Source.Media?.Probe.VideoStreams.FirstOrDefault() is not { } video)
            {
                continue;
            }

            var visibleDuration = visibleEnd - visibleStart;
            var count = Math.Clamp(
                (int)Math.Ceiling(viewportThumbnailCount * visibleDuration / viewportDuration),
                1,
                viewportThumbnailCount);
            var sourceVisibleStart = clip.SourceStartSeconds + (visibleStart - clip.TimelineStartSeconds);
            var sourceVisibleEnd = clip.SourceStartSeconds + (visibleEnd - clip.TimelineStartSeconds);
            var cellDuration = (sourceVisibleEnd - sourceVisibleStart) / count;
            for (var index = 0; index < count; index++)
            {
                var cellStart = sourceVisibleStart + (index * cellDuration);
                var cellEnd = index == count - 1
                    ? sourceVisibleEnd
                    : Math.Min(sourceVisibleEnd, cellStart + cellDuration);
                if (cellEnd <= cellStart)
                {
                    continue;
                }

                var cellMidpoint = cellStart + ((cellEnd - cellStart) / 2);
                var cacheKey = TimelineFrameCacheKey.Create(
                    clip.SourcePath,
                    video.Index,
                    TimelineThumbnailSize,
                    bucketDuration,
                    cellMidpoint);
                var decodeTimestamp = Math.Clamp(
                    cacheKey.TimestampSeconds,
                    0,
                    Math.Max(0, clip.Source.SourceDurationSeconds - clip.Source.FrameStepSeconds));
                requests.Add(new SequenceFrameRequest(
                    clip,
                    video.Index,
                    cellStart,
                    cellEnd,
                    cellMidpoint,
                    decodeTimestamp,
                    cacheKey));
            }
        }

        return requests;
    }

    private bool ApplyCachedSequenceFrames(
        IReadOnlyList<VideoClipViewModel> clips,
        IReadOnlyList<SequenceFrameRequest> requests,
        bool requireComplete)
    {
        var generated = new Dictionary<VideoClipViewModel, List<TimelineThumbnailFrame>>();
        var cachedCount = 0;
        try
        {
            foreach (var request in requests)
            {
                if (!_timelineFrameCache.TryGet(request.CacheKey, out var encodedImage))
                {
                    if (requireComplete)
                    {
                        return false;
                    }

                    continue;
                }

                using var stream = new MemoryStream(encodedImage, writable: false);
                var frame = new TimelineThumbnailFrame(
                    request.CellStart,
                    request.CellEnd,
                    request.CellMidpoint,
                    new Bitmap(stream));
                if (!generated.TryGetValue(request.Clip, out var frames))
                {
                    frames = [];
                    generated.Add(request.Clip, frames);
                }

                frames.Add(frame);
                cachedCount++;
            }

            if (cachedCount == 0)
            {
                return false;
            }

            foreach (var clip in clips)
            {
                if (!VideoClips.Contains(clip))
                {
                    continue;
                }

                if (generated.Remove(clip, out var frames))
                {
                    clip.SetTimelineThumbnails(frames.OrderBy(frame => frame.Start).ToArray());
                    frames.Clear();
                }
                else if (requireComplete)
                {
                    clip.SetTimelineThumbnails([]);
                }

                clip.IsTimelineLoading = false;
            }

            _sequenceTimelineVisualRevision++;
            OnPropertyChanged(nameof(SequenceTimelineVisualRevision));
            return true;
        }
        finally
        {
            foreach (var frames in generated.Values)
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }
        }
    }

    private void StartCachedTimelineHoverPreview(double timelineSeconds)
    {
        if (timelineSeconds < 0 || _frameDecoder is null)
        {
            ClearTimelineHoverPreview();
            return;
        }

        var timelineTime = SequenceTimeFromSeconds(timelineSeconds);
        var clip = FindClipAtTimelineTime(timelineTime);
        if (clip?.Source.Media?.Probe.VideoStreams.FirstOrDefault() is not { } video)
        {
            ClearTimelineHoverPreview();
            return;
        }

        var sourceTime = clip.SourceStart +
                         (timelineTime - clip.TimelineStart);
        var boundedSourceSeconds = Math.Min(sourceTime.TotalSeconds, clip.SourceEndSeconds);
        var exactHoverBucketDuration = Math.Max(clip.Source.FrameStepSeconds, 1d / 120d);
        var exactKey = TimelineFrameCacheKey.Create(
            clip.SourcePath,
            video.Index,
            TimelineHoverSize,
            exactHoverBucketDuration,
            boundedSourceSeconds);
        if (_timelineFrameCache.TryGet(exactKey, out var exactImage))
        {
            CancelTimelineHoverRequest();
            ShowCachedHover(exactKey, exactImage);
            return;
        }

        var maximumDistance = Math.Max(
            0.5,
            SequenceTimelineViewportDuration / SequenceViewportThumbnailCount);
        if (_timelineFrameCache.TryGetNearest(
                clip.SourcePath,
                video.Index,
                boundedSourceSeconds,
                maximumDistance,
                out var nearestKey,
                out var nearestImage))
        {
            ShowCachedHover(nearestKey, nearestImage);
        }
        else
        {
            _timelineHoverCacheKey = null;
            TimelineHoverPreviewImage = null;
        }

        if (_timelineHoverRequestKey == exactKey && _timelineHoverCancellation is not null)
        {
            return;
        }

        CancelTimelineHoverRequest();
        var request = new CancellationTokenSource();
        _timelineHoverCancellation = request;
        _timelineHoverRequestKey = exactKey;
        _ = RefreshCachedTimelineHoverPreviewAsync(clip, video.Index, sourceTime, exactKey, request);
    }

    private async Task RefreshCachedTimelineHoverPreviewAsync(
        VideoClipViewModel clip,
        int videoStreamIndex,
        MediaTime sourceTime,
        TimelineFrameCacheKey cacheKey,
        CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            // Warm filmstrip frames display immediately. Exact FFmpeg refinement is
            // launched as soon as the pointer settles briefly on a source frame.
            await Task.Delay(TimeSpan.FromMilliseconds(45), token);
            await _analysisSlots.WaitAsync(token);
            DecodedFrame decoded;
            try
            {
                decoded = await _frameDecoder!.DecodeAsync(
                    clip.SourcePath,
                    videoStreamIndex,
                    Min(sourceTime, clip.SourceEnd),
                    TimelineHoverSize,
                    token);
            }
            finally
            {
                _analysisSlots.Release();
            }

            _timelineFrameCache.Set(cacheKey, decoded.EncodedImage);
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_timelineHoverCancellation, request))
            {
                ShowCachedHover(cacheKey, decoded.EncodedImage.ToArray());
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is FrameDecodeException or IOException)
        {
            // Keep the approximate cached frame if exact hover refinement fails.
        }
        finally
        {
            if (ReferenceEquals(_timelineHoverCancellation, request))
            {
                _timelineHoverCancellation = null;
                _timelineHoverRequestKey = null;
            }

            request.Dispose();
        }
    }

    private void ClearTimelineHoverPreview()
    {
        CancelTimelineHoverRequest();
        _timelineHoverCacheKey = null;
        TimelineHoverPreviewImage = null;
    }

    private void CancelTimelineHoverRequest()
    {
        _timelineHoverCancellation?.Cancel();
        _timelineHoverCancellation = null;
        _timelineHoverRequestKey = null;
    }

    private void ShowCachedHover(TimelineFrameCacheKey cacheKey, byte[] encodedImage)
    {
        if (_timelineHoverCacheKey == cacheKey)
        {
            return;
        }

        using var stream = new MemoryStream(encodedImage, writable: false);
        TimelineHoverPreviewImage = new Bitmap(stream);
        _timelineHoverCacheKey = cacheKey;
    }

    private sealed record SequenceFrameRequest(
        VideoClipViewModel Clip,
        int VideoStreamIndex,
        double CellStart,
        double CellEnd,
        double CellMidpoint,
        double DecodeTimestamp,
        TimelineFrameCacheKey CacheKey);
}
