using ClipEdit.Media.Mpv.Native;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvPlaybackSpeedPolicyTests
{
    [Theory]
    [InlineData(0.01, "0.01")]
    [InlineData(1, "1")]
    [InlineData(100, "100")]
    public void Playback_speed_uses_the_mpv_speed_property(double speed, string expectedValue)
    {
        var property = MpvClient.GetPlaybackSpeedProperty(speed);

        Assert.Equal("speed", property.Name);
        Assert.Equal(expectedValue, property.Value);
    }

    [Theory]
    [InlineData(0.009)]
    [InlineData(100.01)]
    public void Playback_speed_rejects_values_outside_the_editor_range(double speed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MpvClient.GetPlaybackSpeedProperty(speed));
    }
}
