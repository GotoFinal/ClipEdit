using ClipEdit.Media.Mpv.Native;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvColorManagementPolicyTests
{
    [Fact]
    public void Embedded_preview_targets_the_color_space_of_avalonias_sdr_surface()
    {
        var options = MpvClient.GetInitializationOptions().ToDictionary();

        Assert.Equal("bt.709", options["target-prim"]);
        Assert.Equal("srgb", options["target-trc"]);
        Assert.Equal("auto", options["target-peak"]);
        Assert.Equal("auto", options["tone-mapping"]);
        Assert.Equal("perceptual", options["gamut-mapping-mode"]);
        Assert.Equal("auto", options["hdr-compute-peak"]);
    }
}
