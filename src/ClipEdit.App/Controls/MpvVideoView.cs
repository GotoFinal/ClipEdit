using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Mpv;
using ClipEdit.Media.Preview;

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

    public static readonly StyledProperty<IReadOnlyList<PreviewAudioTrack>> AudioTracksProperty =
        AvaloniaProperty.Register<MpvVideoView, IReadOnlyList<PreviewAudioTrack>>(
            nameof(AudioTracks),
            defaultValue: Array.Empty<PreviewAudioTrack>());

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
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _seekCancellation;
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
        AudioTracksProperty.Changed.AddClassHandler<MpvVideoView>(
            static (view, _) => view.StartAudioMixChange());
    }

    public MpvVideoView()
    {
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _positionTimer.Tick += OnPositionTimerTick;
    }

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

    public IReadOnlyList<PreviewAudioTrack> AudioTracks
    {
        get => GetValue(AudioTracksProperty);
        set => SetValue(AudioTracksProperty, value);
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
        if (!IsPlaybackAvailable)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (IsPaused && _isEndOfFile && _engine is not null)
        {
            _updatingPositionFromPlayback = true;
            try
            {
                SetCurrentValue(PositionProperty, MediaTime.Zero);
            }
            finally
            {
                _updatingPositionFromPlayback = false;
            }

            await _engine.SeekAsync(MediaTime.Zero, cancellationToken);
            _isEndOfFile = false;
        }

        SetCurrentValue(IsPausedProperty, !IsPaused);
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
        if (_engine is null)
        {
            return;
        }

        try
        {
            _renderContext ??= _engine.CreateOpenGlRenderContext(
                gl.GetProcAddress,
                RequestRenderFromNativeCallback);
            if (!_mediaLoaded)
            {
                return;
            }

            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var width = Math.Max(1, (int)(Bounds.Width * scaling));
            var height = Math.Max(1, (int)(Bounds.Height * scaling));
            _renderContext.Render(framebuffer, width, height);
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
        if (_shutdownStarted || _engineTask is null)
        {
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _ = LoadAsync(_loadCancellation.Token);
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
            await engine.SeekAsync(Position, cancellationToken);
            await engine.SetVolumeAsync(Volume, cancellationToken);
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

        _seekCancellation?.Cancel();
        _seekCancellation?.Dispose();
        _seekCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _ = SeekAsync(_seekCancellation.Token);
    }

    private async Task SeekAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(45), cancellationToken);
            await _engine!.SeekAsync(Position, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer playhead position superseded this seek.
        }
        catch (Exception exception)
        {
            SetFailure($"Live preview seek failed: {exception.Message}");
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
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
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
                _updatingPositionFromPlayback = true;
                try
                {
                    SetCurrentValue(PositionProperty, snapshot.Position.Value);
                }
                finally
                {
                    _updatingPositionFromPlayback = false;
                }
            }

            if (snapshot.IsEndOfFile)
            {
                _isEndOfFile = true;
                _positionTimer.Stop();
                SetCurrentValue(IsPausedProperty, true);
                PlaybackStatus = "Playback ended; press Play to restart";
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
