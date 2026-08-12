namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvApiVersionTests
{
    [Fact]
    public void FromPacked_splits_major_and_minor_words()
    {
        var version = MpvApiVersion.FromPacked(0x0002_0005);

        Assert.Equal(2, version.Major);
        Assert.Equal(5, version.Minor);
        Assert.Equal("2.5", version.ToString());
    }
}
