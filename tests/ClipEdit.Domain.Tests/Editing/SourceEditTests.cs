using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Domain.Tests.Editing;

public sealed class SourceEditTests
{
    [Fact]
    public void New_edit_keeps_the_full_non_empty_source()
    {
        var edit = new SourceEdit(new MediaTime(10, 1));

        Assert.True(edit.IsUnedited);
        Assert.Equal(new MediaTime(10, 1), edit.OutputDuration);
        Assert.Equal(new MediaRange(MediaTime.Zero, new MediaTime(10, 1)), Assert.Single(edit.KeptRanges));
    }

    [Fact]
    public void Removing_the_middle_preserves_two_ordered_ranges()
    {
        var edit = new SourceEdit(new MediaTime(10, 1));

        var result = edit.Remove(new MediaRange(new MediaTime(3, 1), new MediaTime(7, 1)));

        Assert.Equal<MediaRange>(
            [
                new MediaRange(MediaTime.Zero, new MediaTime(3, 1)),
                new MediaRange(new MediaTime(7, 1), new MediaTime(10, 1)),
            ],
            result.KeptRanges);
        Assert.Equal(new MediaTime(6, 1), result.OutputDuration);
        Assert.False(result.Contains(new MediaTime(5, 1)));
    }

    [Fact]
    public void Removal_is_applied_to_already_split_ranges()
    {
        var edit = new SourceEdit(new MediaTime(10, 1))
            .Remove(new MediaRange(new MediaTime(3, 1), new MediaTime(4, 1)))
            .Remove(new MediaRange(new MediaTime(6, 1), new MediaTime(7, 1)));

        var result = edit.Remove(new MediaRange(new MediaTime(2, 1), new MediaTime(8, 1)));

        Assert.Equal<MediaRange>(
            [
                new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
                new MediaRange(new MediaTime(8, 1), new MediaTime(10, 1)),
            ],
            result.KeptRanges);
    }

    [Fact]
    public void Removal_is_clamped_to_the_source_bounds()
    {
        var edit = new SourceEdit(new MediaTime(10, 1));

        var result = edit.Remove(new MediaRange(new MediaTime(-5, 1), new MediaTime(2, 1)));

        Assert.Equal(
            new MediaRange(new MediaTime(2, 1), new MediaTime(10, 1)),
            Assert.Single(result.KeptRanges));
    }

    [Fact]
    public void Removing_everything_produces_an_empty_edit()
    {
        var edit = new SourceEdit(new MediaTime(10, 1));

        var result = edit.Remove(new MediaRange(MediaTime.Zero, new MediaTime(10, 1)));

        Assert.True(result.IsEmpty);
        Assert.Equal(MediaTime.Zero, result.OutputDuration);
    }

    [Fact]
    public void Empty_or_disjoint_removal_returns_the_same_instance()
    {
        var edit = new SourceEdit(new MediaTime(10, 1));

        Assert.Same(edit, edit.Remove(new MediaRange(new MediaTime(3, 1), new MediaTime(3, 1))));
        Assert.Same(edit, edit.Remove(new MediaRange(new MediaTime(12, 1), new MediaTime(14, 1))));
    }

    [Fact]
    public void Reset_restores_the_full_source()
    {
        var edit = new SourceEdit(new MediaTime(10, 1))
            .Remove(new MediaRange(new MediaTime(2, 1), new MediaTime(8, 1)));

        Assert.True(edit.Reset().IsUnedited);
    }

    [Fact]
    public void Keep_only_intersects_the_selection_with_existing_kept_ranges()
    {
        var edit = new SourceEdit(new MediaTime(10, 1))
            .Remove(new MediaRange(new MediaTime(4, 1), new MediaTime(6, 1)));

        var result = edit.KeepOnly(
            new MediaRange(new MediaTime(2, 1), new MediaTime(8, 1)));

        Assert.Equal<MediaRange>(
        [
            new MediaRange(new MediaTime(2, 1), new MediaTime(4, 1)),
            new MediaRange(new MediaTime(6, 1), new MediaTime(8, 1)),
        ], result.KeptRanges);
    }

    [Fact]
    public void Keep_only_outside_existing_content_produces_an_empty_edit()
    {
        var edit = new SourceEdit(new MediaTime(10, 1))
            .Remove(new MediaRange(new MediaTime(2, 1), new MediaTime(8, 1)));

        var result = edit.KeepOnly(
            new MediaRange(new MediaTime(3, 1), new MediaTime(7, 1)));

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Negative_source_duration_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceEdit(new MediaTime(-1, 1)));
    }

    [Fact]
    public void Saved_kept_ranges_can_be_restored_after_validation()
    {
        var ranges = new[]
        {
            new MediaRange(MediaTime.Zero, new MediaTime(2, 1)),
            new MediaRange(new MediaTime(8, 1), new MediaTime(10, 1)),
        };

        var edit = SourceEdit.FromKeptRanges(new MediaTime(10, 1), ranges);

        Assert.Equal<MediaRange>(ranges, edit.KeptRanges);
    }

    [Fact]
    public void Invalid_saved_ranges_are_rejected()
    {
        var overlapping = new[]
        {
            new MediaRange(MediaTime.Zero, new MediaTime(6, 1)),
            new MediaRange(new MediaTime(5, 1), new MediaTime(10, 1)),
        };

        Assert.Throws<ArgumentException>(() =>
            SourceEdit.FromKeptRanges(new MediaTime(10, 1), overlapping));
    }
}
