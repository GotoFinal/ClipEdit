using ClipEdit.App.Controls;

namespace ClipEdit.App.Tests.Controls;

public sealed class PreviewLoadGateTests
{
    [Fact]
    public void Load_waits_for_both_engine_and_render_context()
    {
        var gate = new PreviewLoadGate();
        gate.Request();

        Assert.False(gate.TryConsume(
            isShuttingDown: false,
            isEngineReady: false,
            isRenderContextReady: false));
        Assert.True(gate.IsPending);
        Assert.False(gate.TryConsume(
            isShuttingDown: false,
            isEngineReady: true,
            isRenderContextReady: false));
        Assert.True(gate.IsPending);

        Assert.True(gate.TryConsume(
            isShuttingDown: false,
            isEngineReady: true,
            isRenderContextReady: true));
        Assert.False(gate.IsPending);
    }

    [Fact]
    public void Each_source_change_can_start_exactly_one_load()
    {
        var gate = new PreviewLoadGate();

        gate.Request();
        Assert.True(gate.TryConsume(false, true, true));
        Assert.False(gate.TryConsume(false, true, true));

        gate.Request();
        Assert.True(gate.TryConsume(false, true, true));
    }

    [Fact]
    public void Shutdown_never_consumes_a_pending_load()
    {
        var gate = new PreviewLoadGate();
        gate.Request();

        Assert.False(gate.TryConsume(
            isShuttingDown: true,
            isEngineReady: true,
            isRenderContextReady: true));
        Assert.True(gate.IsPending);
    }
}
