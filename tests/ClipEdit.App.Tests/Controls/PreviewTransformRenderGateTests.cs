using ClipEdit.App.Controls;

namespace ClipEdit.App.Tests.Controls;

public sealed class PreviewTransformRenderGateTests
{
    [Fact]
    public async Task Submitted_revision_waits_for_a_render()
    {
        var gate = new PreviewTransformRenderGate();
        gate.MarkSubmitted(3);

        var waiting = gate.WaitForRenderedAsync(3, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        gate.MarkRendered();

        await waiting;
    }

    [Fact]
    public async Task One_render_completes_all_revisions_submitted_before_it()
    {
        var gate = new PreviewTransformRenderGate();
        gate.MarkSubmitted(3);
        var first = gate.WaitForRenderedAsync(3, TestContext.Current.CancellationToken);
        gate.MarkSubmitted(8);
        var latest = gate.WaitForRenderedAsync(8, TestContext.Current.CancellationToken);

        gate.MarkRendered();

        await Task.WhenAll(first, latest);
        await gate.WaitForRenderedAsync(5, TestContext.Current.CancellationToken);
    }
}
