namespace ClipEdit.App.Controls;

internal sealed class PreviewLoadGate
{
    public bool IsPending { get; private set; }

    public void Request()
    {
        IsPending = true;
    }

    public bool TryConsume(
        bool isShuttingDown,
        bool isEngineReady,
        bool isRenderContextReady)
    {
        if (!IsPending || isShuttingDown || !isEngineReady || !isRenderContextReady)
        {
            return false;
        }

        IsPending = false;
        return true;
    }
}
