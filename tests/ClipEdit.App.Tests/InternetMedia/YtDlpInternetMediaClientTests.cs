using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Tests.InternetMedia;

public sealed class YtDlpInternetMediaClientTests
{
    [Theory]
    [InlineData("__CLIPEDIT_PROGRESS__ 42.5%|1048576|2097152|12", 0.425, 1048576L, 2097152L, 12)]
    [InlineData("[download] __CLIPEDIT_PROGRESS__NA|NA|NA|3", null, null, null, 3)]
    public void Parses_machine_progress(
        string line,
        double? fraction,
        long? downloaded,
        long? total,
        double remainingSeconds)
    {
        Assert.True(YtDlpInternetMediaClient.TryParseProgress(line, out var progress));
        Assert.Equal(fraction, progress!.Fraction);
        Assert.Equal(downloaded, progress.DownloadedBytes);
        Assert.Equal(total, progress.TotalBytes);
        Assert.Equal(TimeSpan.FromSeconds(remainingSeconds), progress.Remaining);
    }

    [Fact]
    public void Ignores_unrelated_output()
    {
        Assert.False(YtDlpInternetMediaClient.TryParseProgress("[download] Destination: media.webm", out _));
    }
}
