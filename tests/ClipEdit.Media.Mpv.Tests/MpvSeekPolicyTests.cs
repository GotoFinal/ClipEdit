using ClipEdit.Media.Mpv.Native;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvSeekPolicyTests
{
    [Theory]
    [InlineData(false, "absolute+keyframes")]
    [InlineData(true, "absolute+exact")]
    public void Interactive_and_refinement_seeks_use_distinct_mpv_precision_modes(
        bool exact,
        string expected)
    {
        Assert.Equal(expected, MpvClient.GetSeekModeArgument(exact));
    }
}
