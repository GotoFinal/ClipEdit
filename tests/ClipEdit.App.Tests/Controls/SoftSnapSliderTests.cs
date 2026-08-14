using System.Reflection;
using Avalonia.Controls;
using ClipEdit.App.Controls;

namespace ClipEdit.App.Tests.Controls;

public sealed class SoftSnapSliderTests
{
    [Fact]
    public void Derived_slider_uses_the_base_slider_theme()
    {
        var styleKeyProperty = typeof(SoftSnapSlider).GetProperty(
            "StyleKeyOverride",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(styleKeyProperty);
        Assert.Equal(typeof(Slider), styleKeyProperty.GetValue(new SoftSnapSlider()));
    }

    [Fact]
    public void Nearby_values_snap_to_the_nearest_anchor()
    {
        Assert.Equal(50, SoftSnapSlider.CalculateSoftSnap(
            49.2,
            [25, 50, 75],
            threshold: 1.25,
            bypass: false));
        Assert.Equal(47, SoftSnapSlider.CalculateSoftSnap(
            47,
            [25, 50, 75],
            threshold: 1.25,
            bypass: false));
    }

    [Fact]
    public void Shift_bypasses_soft_snapping()
    {
        Assert.Equal(49.2, SoftSnapSlider.CalculateSoftSnap(
            49.2,
            [25, 50, 75],
            threshold: 1.25,
            bypass: true));
    }
}
