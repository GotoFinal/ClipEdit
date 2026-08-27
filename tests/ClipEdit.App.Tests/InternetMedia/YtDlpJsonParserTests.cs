using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Tests.InternetMedia;

public sealed class YtDlpJsonParserTests
{
    [Fact]
    public void Parses_metadata_and_builds_distinct_quality_choices()
    {
        var info = YtDlpJsonParser.Parse(
            new Uri("https://example.test/watch/1"),
            """
            {
              "title": "Example clip",
              "webpage_url": "https://example.test/watch/1",
              "extractor_key": "Generic",
              "duration": 12.5,
              "formats": [
                { "format_id": "v720", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "width": 1280, "height": 720, "fps": 30, "filesize_approx": 1000 },
                { "format_id": "v1080", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "width": 1920, "height": 1080, "fps": 30 },
                { "format_id": "a128", "ext": "m4a", "vcodec": "none", "acodec": "mp4a", "abr": 128 },
                { "format_id": "a192", "ext": "m4a", "vcodec": "none", "acodec": "mp4a", "abr": 192 }
              ]
            }
            """);

        Assert.Equal("Example clip", info.Title);
        Assert.Equal("Generic", info.Extractor);
        Assert.Equal(TimeSpan.FromSeconds(12.5), info.Duration);
        Assert.True(info.HasVideoQualityChoice);
        Assert.True(info.HasAudioQualityChoice);
        Assert.Equal([null, 1080, 720], info.CreateVideoQualityChoices().Select(static choice => choice.MaximumHeight));
        Assert.Equal([null, 192, 128], info.CreateAudioQualityChoices().Select(static choice => choice.MaximumBitrateKbps));
    }

    [Fact]
    public void Rejects_metadata_without_downloadable_formats()
    {
        var exception = Assert.Throws<InternetMediaException>(() => YtDlpJsonParser.Parse(
            new Uri("https://example.test/watch/1"),
            "{\"title\":\"Unavailable\",\"formats\":[]}"));

        Assert.Contains("downloadable media", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
