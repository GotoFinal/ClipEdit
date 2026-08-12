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
    private readonly MpvNativeLibrary _native;
    private readonly Thread _ownerThread;
    private int _disposeStarted;
    private int _state = (int)PreviewState.Initializing;

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
        InvokeAsync(client => client.Seek(position), cancellationToken);

    public Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken) =>
        InvokeAsync(
            client =>
            {
                client.SetPaused(isPaused);
                Volatile.Write(ref _state, (int)(isPaused ? PreviewState.Paused : PreviewState.Playing));
            },
            cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) =>
        InvokeAsync(client => client.SetVolume(volume), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _workItems.CompleteAdding();
        }

        await _stopped.Task.ConfigureAwait(false);
        _workItems.Dispose();
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
            _native.Dispose();
            Volatile.Write(ref _state, (int)PreviewState.Disposed);
            _stopped.TrySetResult();
        }
    }

    private sealed record WorkItem(
        Action<MpvClient> Action,
        TaskCompletionSource Completion,
        CancellationToken CancellationToken);
}
