using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Tests.InternetMedia;

public sealed class YtDlpArgumentsTests
{
    [Fact]
    public void Probe_is_single_item_json_and_never_reads_user_configuration()
    {
        var arguments = YtDlpArguments.CreateProbe(new Uri("https://example.test/video.mp4"));

        Assert.Contains("--ignore-config", arguments);
        Assert.Contains("--no-playlist", arguments);
        Assert.Contains("--skip-download", arguments);
        Assert.Contains("--dump-single-json", arguments);
        Assert.Equal("https://example.test/video.mp4", arguments[^1]);
    }

    [Theory]
    [InlineData(null, null, "bv*+ba/b")]
    [InlineData(720, null, "bv*[height<=720]+ba/b[height<=720]/b")]
    [InlineData(null, 128, "bv*+ba[abr<=128]/bv*+ba/b/b")]
    [InlineData(1080, 192, "bv*[height<=1080]+ba[abr<=192]/bv*[height<=1080]+ba/b[height<=1080]/b")]
    public void Format_selector_applies_requested_caps_with_safe_fallbacks(
        int? height,
        int? audioBitrate,
        string expected)
    {
        Assert.Equal(expected, YtDlpArguments.CreateFormatSelector(height, audioBitrate));
    }

    [Fact]
    public void Download_is_resumable_concurrent_and_uses_configured_ffmpeg()
    {
        var info = new InternetMediaInfo(
            new Uri("https://example.test/watch/1"),
            "Example",
            "Generic",
            TimeSpan.FromMinutes(1),
            []);
        var request = new InternetMediaDownloadRequest(
            info,
            new InternetVideoQualityChoice(720, "Up to 720p"),
            new InternetAudioQualityChoice(128, "Up to 128 kbps"));
        var output = Path.Combine(Path.GetTempPath(), "clipedit-internet-arguments");
        var ffmpeg = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

        var arguments = YtDlpArguments.CreateDownload(request, output, ffmpeg, 6);

        Assert.Contains("--continue", arguments);
        Assert.Contains("--part", arguments);
        Assert.Equal("6", ValueAfter(arguments, "--concurrent-fragments"));
        Assert.Equal(Path.GetFullPath(ffmpeg), ValueAfter(arguments, "--ffmpeg-location"));
        Assert.Equal(Path.GetFullPath(output), ValueAfter(arguments, "--paths"));
        Assert.Contains(YtDlpArguments.ProgressPrefix, ValueAfter(arguments, "--progress-template"));
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
