using ClipEdit.App.Controls;

namespace ClipEdit.App.Tests.Controls;

public sealed class PreviewRenderRequestGateTests
{
    [Fact]
    public void Repeated_native_updates_queue_only_one_ui_render_until_consumed()
    {
        var gate = new PreviewRenderRequestGate();

        Assert.True(gate.TryQueue());
        Assert.False(gate.TryQueue());
        Assert.False(gate.TryQueue());

        gate.Complete();

        Assert.True(gate.TryQueue());
    }

    [Fact]
    public void Concurrent_native_updates_have_a_single_winner()
    {
        var gate = new PreviewRenderRequestGate();
        var accepted = 0;

        Parallel.For(0, 100, _ =>
        {
            if (gate.TryQueue())
            {
                Interlocked.Increment(ref accepted);
            }
        });

        Assert.Equal(1, accepted);
    }
}
