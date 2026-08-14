using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Mpv;
using ClipEdit.Media.Preview;
using DomainPixelSize = ClipEdit.Domain.Geometry.PixelSize;

namespace ClipEdit.App.Controls;

public sealed class MpvVideoView : OpenGlControlBase
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<MpvVideoView, string?>(nameof(SourcePath));

    public static readonly StyledProperty<MediaTime> PositionProperty =
        AvaloniaProperty.Register<MpvVideoView, MediaTime>(nameof(Position), MediaTime.Zero);

    public static readonly StyledProperty<bool> IsPausedProperty =
        AvaloniaProperty.Register<MpvVideoView, bool>(nameof(IsPaused), defaultValue: true);

    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<MpvVideoView, double>(nameof(Volume), defaultValue: 1);

    public static readonly StyledProperty<double> PlaybackSpeedProperty =
        AvaloniaProperty.Register<MpvVideoView, double>(nameof(PlaybackSpeed), defaultValue: 1);

    public static readonly StyledProperty<DomainPixelSize> SourceVideoSizeProperty =
        AvaloniaProperty.Register<MpvVideoView, DomainPixelSize>(
            nameof(SourceVideoSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<DomainPixelSize> CanvasSizeProperty =
        AvaloniaProperty.Register<MpvVideoView, DomainPixelSize>(
            nameof(CanvasSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<ClipCanvasTransform> CanvasTransformProperty =
        AvaloniaProperty.Register<MpvVideoView, ClipCanvasTransform>(
            nameof(CanvasTransform),
            ClipCanvasTransform.Identity);

    public static readonly StyledProperty<bool> IsInteractiveTransformActiveProperty =
        AvaloniaProperty.Register<MpvVideoView, bool>(nameof(IsInteractiveTransformActive));

    public static readonly StyledProperty<IReadOnlyList<PreviewAudioTrack>> AudioTracksProperty =
        AvaloniaProperty.Register<MpvVideoView, IReadOnlyList<PreviewAudioTrack>>(
            nameof(AudioTracks),
            defaultValue: Array.Empty<PreviewAudioTrack>());

    public static readonly StyledProperty<IReadOnlyList<MediaRange>> PlaybackRangesProperty =
        AvaloniaProperty.Register<MpvVideoView, IReadOnlyList<MediaRange>>(
            nameof(PlaybackRanges),
            defaultValue: Array.Empty<MediaRange>());

    public static readonly StyledProperty<bool> HasCutsProperty =
        AvaloniaProperty.Register<MpvVideoView, bool>(nameof(HasCuts));

    public static readonly DirectProperty<MpvVideoView, bool> IsPlaybackAvailableProperty =
        AvaloniaProperty.RegisterDirect<MpvVideoView, bool>(
            nameof(IsPlaybackAvailable),
            view => view.IsPlaybackAvailable);

    public static readonly DirectProperty<MpvVideoView, string> PlaybackStatusProperty =
        AvaloniaProperty.RegisterDirect<MpvVideoView, string>(
            nameof(PlaybackStatus),
            view => view.PlaybackStatus);

    public static readonly DirectProperty<MpvVideoView, string> PlayButtonTextProperty =
        AvaloniaProperty.RegisterDirect<MpvVideoView, string>(
            nameof(PlayButtonText),
            view => view.PlayButtonText);

    public static readonly DirectProperty<MpvVideoView, string> DecoderStatusProperty =
        AvaloniaProperty.RegisterDirect<MpvVideoView, string>(
            nameof(DecoderStatus),
            view => view.DecoderStatus);

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly PreviewLoadGate _loadGate = new();
    private readonly PreviewRenderRequestGate _renderRequestGate = new();
    private readonly PreviewTransformRenderGate _transformRenderGate = new();
    private CancellationTokenSource? _loadCancellation;
    private Task _loadTask = Task.CompletedTask;
    private CancellationTokenSource? _seekCancellation;
    private Task _seekTask = Task.CompletedTask;
    private MediaTime _pendingSeekPosition;
    private int _seekRevision;
    private bool _seekLoopRunning;
    private bool _suppressSeekRestart;
    private CancellationTokenSource? _audioMixCancellation;
    private Task<MpvPreviewEngine>? _engineTask;
    private MpvPreviewEngine? _engine;
    private MpvOpenGlRenderContext? _renderContext;
    private bool _isPlaybackAvailable;
    private string _playbackStatus = "Initializing the local preview engine…";
    private string _playButtonText = "▶";
    private string _decoderStatus = "Decoder pending";
    private bool _mediaLoaded;
    private bool _shutdownStarted;
    private bool _positionPollInProgress;
    private bool _updatingPositionFromPlayback;
    private bool _isEndOfFile;
    private int _videoTransformRevision;
    private int _appliedVideoTransformRevision;
    private bool _videoTransformLoopRunning;
    private ClipCanvasTransform _appliedCanvasTransform = ClipCanvasTransform.Identity;

    static MpvVideoView()
    {
        SourcePathProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartLoad());
        PositionProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartSeek());
        IsPausedProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartPauseChange());
        VolumeProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartVolumeChange());
        PlaybackSpeedProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartPlaybackSpeedChange());
        SourceVideoSizeProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartVideoTransformChange());
        CanvasSizeProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartVideoTransformChange());
        CanvasTransformProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartVideoTransformChange());
        IsInteractiveTransformActiveProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.OnInteractiveTransformActiveChanged());
        AudioTracksProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartAudioMixChange());
    }

    public MpvVideoView()
    {
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _positionTimer.Tick += OnPositionTimerTick;
        RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        SizeChanged += (_, _) =>
        {
            UpdateInteractiveCanvasTransform();
            StartVideoTransformChange();
        };
    }

    public event EventHandler? PlaybackCompleted;

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public MediaTime Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public bool IsPaused
    {
        get => GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    public double Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public DomainPixelSize SourceVideoSize
    {
        get => GetValue(SourceVideoSizeProperty);
        set => SetValue(SourceVideoSizeProperty, value);
    }

    public DomainPixelSize CanvasSize
    {
        get => GetValue(CanvasSizeProperty);
        set => SetValue(CanvasSizeProperty, value);
    }

    public ClipCanvasTransform CanvasTransform
    {
        get => GetValue(CanvasTransformProperty);
        set => SetValue(CanvasTransformProperty, value);
    }

    public bool IsInteractiveTransformActive
    {
        get => GetValue(IsInteractiveTransformActiveProperty);
        set => SetValue(IsInteractiveTransformActiveProperty, value);
    }

    public IReadOnlyList<PreviewAudioTrack> AudioTracks
    {
        get => GetValue(AudioTracksProperty);
        set => SetValue(AudioTracksProperty, value);
    }

    public IReadOnlyList<MediaRange> PlaybackRanges
    {
        get => GetValue(PlaybackRangesProperty);
        set => SetValue(PlaybackRangesProperty, value);
    }

    public bool HasCuts
    {
        get => GetValue(HasCutsProperty);
        set => SetValue(HasCutsProperty, value);
    }

    public bool IsPlaybackAvailable
    {
        get => _isPlaybackAvailable;
        private set => SetAndRaise(IsPlaybackAvailableProperty, ref _isPlaybackAvailable, value);
    }

    public string PlaybackStatus
    {
        get => _playbackStatus;
        private set => SetAndRaise(PlaybackStatusProperty, ref _playbackStatus, value);
    }

    public string PlayButtonText
    {
        get => _playButtonText;
        private set => SetAndRaise(PlayButtonTextProperty, ref _playButtonText, value);
    }

    public string DecoderStatus
    {
        get => _decoderStatus;
        private set => SetAndRaise(DecoderStatusProperty, ref _decoderStatus, value);
    }

    public async Task TogglePlaybackAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPaused)
        {
            SetCurrentValue(IsPausedProperty, true);
            return;
        }

        await PlayAsync(cancellationToken);
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await WaitForCurrentLoadAsync(cancellationToken);
        if (!IsPlaybackAvailable || !_mediaLoaded || _engine is null || _shutdownStarted)
        {
            return;
        }

        if (PlaybackRanges.Count == 0)
        {
            PlaybackStatus = "No kept video ranges to play";
            return;
        }

        _suppressSeekRestart = true;
        try
        {
            var canceledSeek = _seekCancellation;
            canceledSeek?.Cancel();
            try
            {
                await _seekTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (canceledSeek?.IsCancellationRequested == true)
            {
                // The ordered playback seek below replaces the scrub request.
            }
        }
        finally
        {
            _suppressSeekRestart = false;
        }

        var target = GetPlaybackStartPosition(Position, PlaybackRanges, _isEndOfFile);
        SetPositionFromPlayback(target);
        await _engine.SeekAsync(target, cancellationToken);
        _isEndOfFile = false;
        await _engine.SetPausedAsync(false, cancellationToken);
        SetCurrentValue(IsPausedProperty, false);
        PlaybackStatus = "Playing timeline preview";
    }

    public async Task StepFrameAsync(
        PreviewFrameStepDirection direction,
        CancellationToken cancellationToken = default)
    {
        if (!IsPlaybackAvailable || !_mediaLoaded || _engine is null || _shutdownStarted)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _positionTimer.Stop();
        SetCurrentValue(IsPausedProperty, true);
        await _engine.SetPausedAsync(true, cancellationToken);
        var positionBeforeStep = Position;
        await _engine.StepFrameAsync(direction, cancellationToken);

        // libmpv accepts the command before the decoded frame/playback clock is updated.
        PreviewPlaybackSnapshot snapshot = default;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            snapshot = await _engine.GetPlaybackSnapshotAsync(cancellationToken);
            if (snapshot.Position is { } updatedPosition && updatedPosition != positionBeforeStep)
            {
                break;
            }
        }

        if (snapshot.Position is { } position)
        {
            SetPositionFromPlayback(position);
        }

        _isEndOfFile = snapshot.IsEndOfFile;
        PlaybackStatus = direction == PreviewFrameStepDirection.Forward
            ? "Stepped one source frame forward"
            : "Stepped one source frame backward (best effort)";
    }

    public async Task ShutdownAsync()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _positionTimer.Stop();
        _lifetimeCancellation.Cancel();
        _loadCancellation?.Cancel();
        _seekCancellation?.Cancel();
        _audioMixCancellation?.Cancel();
        if (_renderContext is null && _engine is not null)
        {
            await _engine.DisposeAsync();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        if (!_shutdownStarted)
        {
            _engineTask ??= InitializeEngineAsync();
            StartLoad();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _ = gl;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        var engine = _engine;
        if (!CanRenderFrame(engine is not null, _mediaLoaded))
        {
            return;
        }

        try
        {
            if (_renderContext is null)
            {
                _renderContext = engine!.CreateOpenGlRenderContext(
                    gl.GetProcAddress,
                    RequestRenderFromNativeCallback);
                TryStartPendingLoad();
            }

            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var width = Math.Max(1, (int)(Bounds.Width * scaling));
            var height = Math.Max(1, (int)(Bounds.Height * scaling));
            _renderContext.Render(framebuffer, width, height);
            _transformRenderGate.MarkRendered();
            if (ShouldContinueRenderingDuringLoad(_loadTask.IsCompleted))
            {
                RequestNextFrameRendering();
            }
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview render failed: {exception.Message}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _ = gl;
        _renderContext?.Dispose();
        _renderContext = null;
        if (_shutdownStarted && _engine is not null)
        {
            _ = _engine.DisposeAsync();
        }
    }

    protected override void OnOpenGlLost()
    {
        SetFailure("The OpenGL preview context was lost. Reopen the window to restore live preview.");
    }

    private async Task<MpvPreviewEngine> InitializeEngineAsync()
    {
        try
        {
            var engine = await MpvPreviewEngine.CreateAsync(
                cancellationToken: _lifetimeCancellation.Token);
            _engine = engine;
            PlaybackStatus = "Live preview is ready";
            StartLoad();
            RequestNextFrameRendering();
            return engine;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview is unavailable: {exception.Message}");
            throw;
        }
    }

    private void StartLoad()
    {
        _loadGate.Request();
        TryStartPendingLoad();
    }

    public double PlaybackSpeed
    {
        get => GetValue(PlaybackSpeedProperty);
        set => SetValue(PlaybackSpeedProperty, value);
    }

    private void TryStartPendingLoad()
    {
        if (!_loadGate.TryConsume(
                _shutdownStarted,
                _engine is not null,
                _renderContext is not null))
        {
            if (!_shutdownStarted && _engineTask is not null && _renderContext is null)
            {
                RequestNextFrameRendering();
            }

            return;
        }

        _loadCancellation?.Cancel();
        _seekCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _loadTask = LoadAsync(_loadCancellation.Token);
    }

    private async Task WaitForCurrentLoadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var pendingLoad = _loadTask;
            await pendingLoad.WaitAsync(cancellationToken);
            if (ReferenceEquals(pendingLoad, _loadTask))
            {
                return;
            }
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var sourcePath = SourcePath;
        _mediaLoaded = false;
        _isEndOfFile = false;
        DecoderStatus = "Decoder pending";
        IsPlaybackAvailable = false;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            PlaybackStatus = "Select a video to start live preview";
            return;
        }

        try
        {
            PlaybackStatus = "Loading live preview…";
            var engine = await _engineTask!.WaitAsync(cancellationToken);
            await engine.LoadAsync(sourcePath, cancellationToken);
            await engine.SetVideoTransformAsync(CalculatePreviewVideoTransform(SourceVideoSize, CanvasSize, CanvasTransform, Bounds.Size), cancellationToken);
            _appliedCanvasTransform = CanvasTransform;
            _appliedVideoTransformRevision = _videoTransformRevision;
            UpdateInteractiveCanvasTransform();
            await engine.SeekAsync(Position, cancellationToken);
            await engine.SetVolumeAsync(Volume, cancellationToken);
            await engine.SetPlaybackSpeedAsync(PlaybackSpeed, cancellationToken);
            string? audioWarning = null;
            try
            {
                await engine.SetAudioTracksAsync(AudioTracks, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                audioWarning = $"Live audio mix unavailable: {exception.Message}";
            }

            await engine.SetPausedAsync(IsPaused, cancellationToken);
            var initialSnapshot = await engine.GetPlaybackSnapshotAsync(cancellationToken);
            UpdateDecoderStatus(initialSnapshot.HardwareDecoder);
            cancellationToken.ThrowIfCancellationRequested();
            _mediaLoaded = true;
            IsPlaybackAvailable = true;
            PlaybackStatus = audioWarning ?? "Live preview is ready";
            RequestNextFrameRendering();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A different media selection superseded this load.
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview could not load this media: {exception.Message}");
        }
    }

    private void StartSeek()
    {
        if (_updatingPositionFromPlayback || !_mediaLoaded || _engine is null || _shutdownStarted)
        {
            return;
        }

        _isEndOfFile = false;
        _pendingSeekPosition = Position;
        _seekRevision++;
        if (_seekLoopRunning)
        {
            return;
        }

        _seekCancellation?.Cancel();
        _seekCancellation?.Dispose();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _seekCancellation = request;
        _seekLoopRunning = true;
        _seekTask = SeekLatestAsync(request);
    }

    private async Task SeekLatestAsync(CancellationTokenSource request)
    {
        var cancellationToken = request.Token;
        var handledRevision = -1;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var revision = _seekRevision;
                var target = _pendingSeekPosition;
                await _engine!.SeekFastAsync(target, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(16), cancellationToken);
                if (revision != _seekRevision)
                {
                    continue;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
                if (revision != _seekRevision)
                {
                    continue;
                }

                await _engine.SeekAsync(_pendingSeekPosition, cancellationToken);
                handledRevision = revision;
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Media reload or shutdown superseded this seek loop.
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview seek failed: {exception.Message}");
        }
        finally
        {
            _seekLoopRunning = false;
            if (ReferenceEquals(_seekCancellation, request))
            {
                _seekCancellation = null;
            }

            request.Dispose();
            if (!_shutdownStarted && _mediaLoaded && !_suppressSeekRestart && handledRevision != _seekRevision)
            {
                StartSeek();
            }
        }
    }

    private void StartPauseChange()
    {
        PlayButtonText = IsPaused ? "▶" : "Ⅱ";
        if (IsPaused)
        {
            _positionTimer.Stop();
        }
        else if (_mediaLoaded)
        {
            _positionTimer.Start();
        }

        if (_mediaLoaded && _engine is not null && !_shutdownStarted)
        {
            _ = ApplyPauseAsync();
        }
    }

    private async Task ApplyPauseAsync()
    {
        try
        {
            await _engine!.SetPausedAsync(IsPaused, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The preview is shutting down.
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview playback failed: {exception.Message}");
        }
    }

    private void StartVolumeChange()
    {
        if (_mediaLoaded && _engine is not null && !_shutdownStarted)
        {
            _ = ApplyVolumeAsync();
        }
    }

    private async Task ApplyVolumeAsync()
    {
        try
        {
            await _engine!.SetVolumeAsync(Volume, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The preview is shutting down.
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview volume failed: {exception.Message}");
        }
    }

    private void StartVideoTransformChange()
    {
        _videoTransformRevision++;
        UpdateInteractiveCanvasTransform();
        TryStartVideoTransformLoop();
    }

    private void OnInteractiveTransformActiveChanged()
    {
        UpdateInteractiveCanvasTransform();
        if (!IsInteractiveTransformActive)
        {
            TryStartVideoTransformLoop();
        }
    }

    private void TryStartVideoTransformLoop()
    {
        if (_mediaLoaded &&
            _engine is not null &&
            !_shutdownStarted &&
            !IsInteractiveTransformActive &&
            !_videoTransformLoopRunning &&
            _appliedVideoTransformRevision != _videoTransformRevision)
        {
            _ = ApplyVideoTransformLoopAsync();
        }
    }

    private async Task ApplyVideoTransformLoopAsync()
    {
        _videoTransformLoopRunning = true;
        try
        {
            while (_mediaLoaded &&
                   _engine is not null &&
                   !_shutdownStarted &&
                   !IsInteractiveTransformActive)
            {
                var handledRevision = _videoTransformRevision;
                var canvasTransform = CanvasTransform;
                var transform = CalculatePreviewVideoTransform(
                    SourceVideoSize,
                    CanvasSize,
                    canvasTransform,
                    Bounds.Size);
                await _engine.QueueVideoTransformAsync(
                    transform,
                    _lifetimeCancellation.Token);
                _transformRenderGate.MarkSubmitted(handledRevision);
                QueueRenderRequest();
                try
                {
                    await _transformRenderGate
                        .WaitForRenderedAsync(handledRevision, _lifetimeCancellation.Token)
                        .WaitAsync(TimeSpan.FromMilliseconds(250), _lifetimeCancellation.Token);
                }
                catch (TimeoutException)
                {
                    // A hidden or occluded window may temporarily stop drawing.
                }

                _appliedCanvasTransform = canvasTransform;
                _appliedVideoTransformRevision = handledRevision;
                UpdateInteractiveCanvasTransform();

                if (IsInteractiveTransformActive || handledRevision == _videoTransformRevision)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview transform failed: {exception.Message}");
        }
        finally
        {
            _videoTransformLoopRunning = false;
        }
    }

    internal static PreviewVideoTransform CalculatePreviewVideoTransform(
        DomainPixelSize sourceSize,
        DomainPixelSize canvasSize,
        ClipCanvasTransform canvasTransform,
        Size viewportSize)
    {
        var viewportWidth = Math.Max(1, viewportSize.Width);
        var viewportHeight = Math.Max(1, viewportSize.Height);
        var radians = canvasTransform.RotationDegrees * Math.PI / 180;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        var rotatedWidth = (sourceSize.Width * cosine) + (sourceSize.Height * sine);
        var rotatedHeight = (sourceSize.Width * sine) + (sourceSize.Height * cosine);
        var canvasDisplayScale = Math.Min(
            viewportWidth / canvasSize.Width,
            viewportHeight / canvasSize.Height);
        var uniformScale = Math.Sqrt(canvasTransform.ScaleX * canvasTransform.ScaleY);
        var desiredPixelScale = uniformScale * canvasDisplayScale;
        var zoomFactor = Math.Clamp(desiredPixelScale, 1d / 1_048_576, 1_048_576);
        var displayedWidth = Math.Max(
            1,
            rotatedWidth * canvasTransform.ScaleX * canvasDisplayScale);
        var displayedHeight = Math.Max(
            1,
            rotatedHeight * canvasTransform.ScaleY * canvasDisplayScale);
        return new PreviewVideoTransform(
            zoomFactor,
            canvasTransform.OffsetX * canvasDisplayScale / displayedWidth,
            canvasTransform.OffsetY * canvasDisplayScale / displayedHeight,
            canvasTransform.RotationDegrees,
            canvasTransform.ScaleX / uniformScale,
            canvasTransform.ScaleY / uniformScale);
    }

    private void UpdateInteractiveCanvasTransform()
    {
        var matrix = CalculateInteractiveCanvasMatrix(
            CanvasSize,
            _appliedCanvasTransform,
            CanvasTransform,
            Bounds.Size);
        RenderTransform = matrix.IsIdentity ? null : new ImmutableTransform(matrix);
    }

    internal static Matrix CalculateInteractiveCanvasMatrix(
        DomainPixelSize canvasSize,
        ClipCanvasTransform applied,
        ClipCanvasTransform desired,
        Size viewportSize)
    {
        var appliedMatrix = CalculateCanvasToViewportMatrix(canvasSize, applied, viewportSize);
        if (!appliedMatrix.TryInvert(out var inverseApplied))
        {
            return Matrix.Identity;
        }

        return inverseApplied * CalculateCanvasToViewportMatrix(canvasSize, desired, viewportSize);
    }

    private static Matrix CalculateCanvasToViewportMatrix(
        DomainPixelSize canvasSize,
        ClipCanvasTransform transform,
        Size viewportSize)
    {
        var viewportWidth = Math.Max(1, viewportSize.Width);
        var viewportHeight = Math.Max(1, viewportSize.Height);
        var displayScale = Math.Min(
            viewportWidth / canvasSize.Width,
            viewportHeight / canvasSize.Height);
        var radians = transform.RotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new Matrix(
            cosine * transform.ScaleX * displayScale,
            sine * transform.ScaleY * displayScale,
            -sine * transform.ScaleX * displayScale,
            cosine * transform.ScaleY * displayScale,
            (viewportWidth / 2) + (transform.OffsetX * displayScale),
            (viewportHeight / 2) + (transform.OffsetY * displayScale));
    }

    private void StartPlaybackSpeedChange()
    {
        if (_mediaLoaded && _engine is not null && !_shutdownStarted)
        {
            _ = ApplyPlaybackSpeedAsync();
        }
    }

    private async Task ApplyPlaybackSpeedAsync()
    {
        try
        {
            await _engine!.SetPlaybackSpeedAsync(PlaybackSpeed, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The preview is shutting down.
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview speed failed: {exception.Message}");
        }
    }

    internal static bool CanRenderFrame(bool isEngineReady, bool isMediaLoaded) =>
        isEngineReady;

    internal static bool ShouldContinueRenderingDuringLoad(bool isLoadCompleted) =>
        !isLoadCompleted;

    private void StartAudioMixChange()
    {
        if (!_mediaLoaded || _engine is null || _shutdownStarted)
        {
            return;
        }

        _audioMixCancellation?.Cancel();
        _audioMixCancellation?.Dispose();
        _audioMixCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _ = ApplyAudioMixAsync(AudioTracks.ToArray(), _audioMixCancellation.Token);
    }

    private async Task ApplyAudioMixAsync(
        IReadOnlyList<PreviewAudioTrack> audioTracks,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            await _engine!.SetAudioTracksAsync(audioTracks, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer mixer state superseded this graph rebuild.
        }
        catch (Exception exception)
        {
            SetPlaybackWarning($"Live audio mix unavailable: {exception.Message}");
        }
    }

    private void RequestRenderFromNativeCallback()
    {
        QueueRenderRequest();
    }

    private void QueueRenderRequest()
    {
        if (!_renderRequestGate.TryQueue())
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                _renderRequestGate.Complete();
                if (!_shutdownStarted)
                {
                    RequestNextFrameRendering();
                }
            },
            DispatcherPriority.Render);
    }

    private async void OnPositionTimerTick(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_positionPollInProgress || IsPaused || !_mediaLoaded || _engine is null)
        {
            return;
        }

        _positionPollInProgress = true;
        try
        {
            var snapshot = await _engine.GetPlaybackSnapshotAsync(_lifetimeCancellation.Token);
            UpdateDecoderStatus(snapshot.HardwareDecoder);
            if (snapshot.Position is not null)
            {
                var decision = GetPlaybackRangeDecision(snapshot.Position.Value, PlaybackRanges);
                if (decision.Action == PlaybackRangeAction.Seek)
                {
                    SetPositionFromPlayback(decision.Target!.Value);
                    await _engine.SeekAsync(decision.Target.Value, _lifetimeCancellation.Token);
                    return;
                }

                if (decision.Action == PlaybackRangeAction.End)
                {
                    SetPositionFromPlayback(PlaybackRanges.Count == 0
                        ? snapshot.Position.Value
                        : PlaybackRanges[^1].End);
                    CompletePlayback("Reached the end of the kept video ranges");
                    return;
                }

                SetPositionFromPlayback(snapshot.Position.Value);
            }

            if (snapshot.IsEndOfFile)
            {
                CompletePlayback("Playback ended; press Play to restart");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            _positionTimer.Stop();
        }
        catch (Exception exception)
        {
            _positionTimer.Stop();
            SetFailure($"Live preview position update failed: {exception.Message}");
        }
        finally
        {
            _positionPollInProgress = false;
        }
    }

    internal static PlaybackRangeDecision GetPlaybackRangeDecision(
        MediaTime position,
        IReadOnlyList<MediaRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        foreach (var range in ranges)
        {
            if (position < range.Start)
            {
                return new PlaybackRangeDecision(PlaybackRangeAction.Seek, range.Start);
            }

            if (position < range.End)
            {
                return new PlaybackRangeDecision(PlaybackRangeAction.Continue, Target: null);
            }
        }

        return new PlaybackRangeDecision(PlaybackRangeAction.End, Target: null);
    }

    internal static MediaTime GetPlaybackStartPosition(
        MediaTime position,
        IReadOnlyList<MediaRange> ranges,
        bool isEndOfFile)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count == 0)
        {
            throw new ArgumentException("At least one playback range is required.", nameof(ranges));
        }

        var decision = GetPlaybackRangeDecision(position, ranges);
        if (isEndOfFile || decision.Action == PlaybackRangeAction.End)
        {
            return ranges[0].Start;
        }

        return decision.Target ?? position;
    }

    private void SetPositionFromPlayback(MediaTime position)
    {
        _updatingPositionFromPlayback = true;
        try
        {
            SetCurrentValue(PositionProperty, position);
        }
        finally
        {
            _updatingPositionFromPlayback = false;
        }
    }

    private void CompletePlayback(string status)
    {
        _isEndOfFile = true;
        _positionTimer.Stop();
        SetCurrentValue(IsPausedProperty, true);
        PlaybackStatus = status;
        PlaybackCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void SetFailure(string message)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                _mediaLoaded = false;
                IsPlaybackAvailable = false;
                PlaybackStatus = message;
            });
    }

    private void SetPlaybackWarning(string message)
    {
        Dispatcher.UIThread.Post(() => PlaybackStatus = message);
    }

    private void UpdateDecoderStatus(string? hardwareDecoder)
    {
        if (string.IsNullOrWhiteSpace(hardwareDecoder))
        {
            DecoderStatus = "Decoder unavailable";
        }
        else if (string.Equals(hardwareDecoder, "no", StringComparison.OrdinalIgnoreCase))
        {
            DecoderStatus = "Software decode";
        }
        else
        {
            DecoderStatus = $"Hardware decode · {hardwareDecoder}";
        }
    }
}

internal enum PlaybackRangeAction
{
    Continue,
    Seek,
    End,
}

internal readonly record struct PlaybackRangeDecision(
    PlaybackRangeAction Action,
    MediaTime? Target);
