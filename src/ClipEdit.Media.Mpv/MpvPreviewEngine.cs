using System.Collections.Concurrent;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Mpv.Native;
using ClipEdit.Media.Preview;

namespace ClipEdit.Media.Mpv;

public sealed class MpvPreviewEngine : IPreviewEngine
{
    private readonly BlockingCollection<WorkItem> _workItems = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifetimeGate = new();
    private readonly MpvNativeLibrary _native;
    private readonly Thread _ownerThread;
    private int _disposeStarted;
    private int _renderContextCount;
    private int _state = (int)PreviewState.Initializing;
    private nint _clientHandle;
    private TaskCompletionSource? _renderContextsReleased;
    private Task? _disposeTask;

    private MpvPreviewEngine(MpvNativeLibrary native)
    {
        _native = native;
        _ownerThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClipEdit libmpv owner",
        };
        _ownerThread.Start();
    }

    public PreviewState State => (PreviewState)Volatile.Read(ref _state);

    public static async Task<MpvPreviewEngine> CreateAsync(
        string? libraryPath = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = MpvNativeLibraryLocator.Find(libraryPath)
            ?? throw new MpvPreviewException(
                "libmpv was not found. Run eng/Get-LibMpv.ps1 or set CLIPEDIT_LIBMPV_PATH.");

        var engine = new MpvPreviewEngine(MpvNativeLibrary.Load(resolvedPath));
        try
        {
            await engine._ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return engine;
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task LoadAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Volatile.Write(ref _state, (int)PreviewState.Loading);
        return InvokeAsync(
            client =>
            {
                client.Load(sourcePath, cancellationToken);
                Volatile.Write(ref _state, (int)PreviewState.Paused);
            },
            cancellationToken);
    }

    public Task SeekAsync(MediaTime position, CancellationToken cancellationToken) =>
        InvokeAsync(client => client.Seek(position, exact: true), cancellationToken);

    public Task SeekFastAsync(MediaTime position, CancellationToken cancellationToken) =>
        InvokeAsync(client => client.Seek(position, exact: false), cancellationToken);

    public async Task<MediaTime?> GetPositionAsync(CancellationToken cancellationToken)
    {
        MediaTime? position = null;
        await InvokeAsync(client => position = client.GetPosition(), cancellationToken).ConfigureAwait(false);
        return position;
    }

    public async Task<PreviewPlaybackSnapshot> GetPlaybackSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = default(PreviewPlaybackSnapshot);
        await InvokeAsync(client => snapshot = client.GetPlaybackSnapshot(), cancellationToken)
            .ConfigureAwait(false);
        return snapshot;
    }

    public Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken) =>
        InvokeAsync(
            client =>
            {
                client.SetPaused(isPaused);
                Volatile.Write(ref _state, (int)(isPaused ? PreviewState.Paused : PreviewState.Playing));
            },
            cancellationToken);

    public Task StepFrameAsync(
        PreviewFrameStepDirection direction,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            client =>
            {
                client.StepFrame(direction);
                Volatile.Write(ref _state, (int)PreviewState.Paused);
            },
            cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) =>
        InvokeAsync(client => client.SetVolume(volume), cancellationToken);

    public Task SetPlaybackSpeedAsync(double speed, CancellationToken cancellationToken) =>
        InvokeAsync(client => client.SetPlaybackSpeed(speed), cancellationToken);

    public Task SetVideoTransformAsync(
        PreviewVideoTransform transform,
        CancellationToken cancellationToken) =>
        InvokeAsync(client => client.SetVideoTransform(transform), cancellationToken);

    public Task SetAudioTracksAsync(
        IReadOnlyList<PreviewAudioTrack> audioTracks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audioTracks);
        var snapshot = audioTracks.ToArray();
        return InvokeAsync(
            client => client.SetAudioTracks(snapshot, cancellationToken),
            cancellationToken);
    }

    public MpvOpenGlRenderContext CreateOpenGlRenderContext(
        Func<string, nint> getProcAddress,
        Action requestRender)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        ArgumentNullException.ThrowIfNull(requestRender);
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted != 0, this);
            if (_clientHandle == nint.Zero || State == PreviewState.Initializing)
            {
                throw new InvalidOperationException("The libmpv client is not ready for rendering.");
            }

            _renderContextCount++;
        }

        return new MpvOpenGlRenderContext(
            _native,
            _clientHandle,
            getProcAddress,
            requestRender,
            ReleaseRenderContext);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private Task InvokeAsync(Action<MpvClient> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _workItems.Add(new WorkItem(action, completion, cancellationToken), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(MpvPreviewEngine));
        }

        return completion.Task;
    }

    private void Run()
    {
        try
        {
            using var client = new MpvClient(_native);
            lock (_lifetimeGate)
            {
                _clientHandle = client.Handle;
            }

            Volatile.Write(ref _state, (int)PreviewState.Idle);
            _ready.TrySetResult();

            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                if (workItem.CancellationToken.IsCancellationRequested)
                {
                    workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                    continue;
                }

                try
                {
                    workItem.Action(client);
                    workItem.Completion.TrySetResult();
                }
                catch (OperationCanceledException exception)
                {
                    workItem.Completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _state, (int)PreviewState.Failed);
                    workItem.Completion.TrySetException(exception);
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _state, (int)PreviewState.Failed);
            _ready.TrySetException(exception);
            while (_workItems.TryTake(out var workItem))
            {
                workItem.Completion.TrySetException(exception);
            }
        }
        finally
        {
            lock (_lifetimeGate)
            {
                _clientHandle = nint.Zero;
            }

            _native.Dispose();
            Volatile.Write(ref _state, (int)PreviewState.Disposed);
            _stopped.TrySetResult();
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task? renderContextsReleased;
        lock (_lifetimeGate)
        {
            _disposeStarted = 1;
            if (_renderContextCount == 0)
            {
                renderContextsReleased = null;
            }
            else
            {
                _renderContextsReleased ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                renderContextsReleased = _renderContextsReleased.Task;
            }
        }

        if (renderContextsReleased is not null)
        {
            await renderContextsReleased.ConfigureAwait(false);
        }

        _workItems.CompleteAdding();
        await _stopped.Task.ConfigureAwait(false);
        _workItems.Dispose();
    }

    private void ReleaseRenderContext()
    {
        lock (_lifetimeGate)
        {
            _renderContextCount--;
            if (_renderContextCount == 0)
            {
                _renderContextsReleased?.TrySetResult();
            }
        }
    }

    private sealed record WorkItem(
        Action<MpvClient> Action,
        TaskCompletionSource Completion,
        CancellationToken CancellationToken);
}
