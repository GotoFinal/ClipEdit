namespace ClipEdit.App.Controls;

internal sealed class PreviewTransformRenderGate
{
    private readonly object _sync = new();
    private TaskCompletionSource _rendered = CreateCompletion();
    private int _submittedRevision;
    private int _renderedRevision;

    public void MarkSubmitted(int revision)
    {
        lock (_sync)
        {
            _submittedRevision = Math.Max(_submittedRevision, revision);
        }
    }

    public void MarkRendered()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            _renderedRevision = _submittedRevision;
            completion = _rendered;
            _rendered = CreateCompletion();
        }

        completion.TrySetResult();
    }

    public async Task WaitForRenderedAsync(int revision, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task rendered;
            lock (_sync)
            {
                if (_renderedRevision >= revision)
                {
                    return;
                }

                rendered = _rendered.Task;
            }

            await rendered.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
