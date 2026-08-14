namespace ClipEdit.App.Controls;

internal sealed class PreviewRenderRequestGate
{
    private int _isQueued;

    public bool TryQueue() => Interlocked.CompareExchange(ref _isQueued, 1, 0) == 0;

    public void Complete() => Volatile.Write(ref _isQueued, 0);
}
