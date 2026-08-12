using ClipEdit.App.Controls;

namespace ClipEdit.App.Tests.Controls;

public sealed class SourceRangeCanvasTests
{
    [Theory]
    [InlineData(2, 8, 2, 8)]
    [InlineData(8, 2, 2, 8)]
    [InlineData(4, 4, 4, 4)]
    public void Drag_selection_is_normalized_in_source_order(
        double anchor,
        double current,
        double expectedStart,
        double expectedEnd)
    {
        var result = SourceRangeCanvas.NormalizeSelection(anchor, current);

        Assert.Equal(expectedStart, result.Start);
        Assert.Equal(expectedEnd, result.End);
    }
}
