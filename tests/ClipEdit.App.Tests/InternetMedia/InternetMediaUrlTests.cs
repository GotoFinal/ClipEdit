using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Tests.InternetMedia;

public sealed class InternetMediaUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc")]
    [InlineData("  https://cdn.example.test/video.mp4  ", "https://cdn.example.test/video.mp4")]
    [InlineData("# comment\r\nhttps://x.com/user/status/123", "https://x.com/user/status/123")]
    public void Parses_supported_http_links(string text, string expected)
    {
        Assert.True(InternetMediaUrl.TryParse(text, out var uri));
        Assert.Equal(expected, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\video.mp4")]
    [InlineData("file:///tmp/video.mp4")]
    [InlineData("https://user:password@example.test/video.mp4")]
    [InlineData("not a url")]
    public void Rejects_non_network_or_credentialed_inputs(string? text)
    {
        Assert.False(InternetMediaUrl.TryParse(text, out _));
    }
}
