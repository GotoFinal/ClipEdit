using ClipEdit.Media.Mpv.Native;
using ClipEdit.Media.Preview;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvVideoTransformPolicyTests
{
    [Fact]
    public void Initial_transform_sets_rotation_before_dependent_display_geometry()
    {
        var transform = new PreviewVideoTransform(1.25, 0.1, -0.2, 17, 1.5, 0.75);

        var changes = MpvClient.GetVideoTransformPropertyChanges(null, transform);

        Assert.Equal(
        [
            "video-rotate",
            "video-zoom",
            "video-scale-x",
            "video-scale-y",
            "video-pan-x",
            "video-pan-y",
        ], changes.Select(change => change.Name));
    }

    [Fact]
    public void Interactive_transform_skips_properties_that_did_not_change()
    {
        var previous = new PreviewVideoTransform(1, 0, 0, 0);
        var current = new PreviewVideoTransform(1.25, 0, 0, 1);

        var changes = MpvClient.GetVideoTransformPropertyChanges(previous, current);

        Assert.Equal(["video-rotate", "video-zoom"], changes.Select(change => change.Name));
        Assert.Empty(MpvClient.GetVideoTransformPropertyChanges(current, current));
    }

    [Fact]
    public void Centered_rotation_changes_only_the_native_rotation_property()
    {
        var previous = new PreviewVideoTransform(0.5, 0, 0, 17);
        var current = new PreviewVideoTransform(0.5, 0, 0, 18);

        var changes = MpvClient.GetVideoTransformPropertyChanges(previous, current);

        Assert.Equal(["video-rotate"], changes.Select(change => change.Name));
    }
}
