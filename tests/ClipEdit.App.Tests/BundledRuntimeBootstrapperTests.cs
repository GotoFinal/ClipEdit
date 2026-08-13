using ClipEdit.App;

namespace ClipEdit.App.Tests;

public sealed class BundledRuntimeBootstrapperTests
{
    [Theory]
    [InlineData(true, "ffmpeg.exe", "ffprobe.exe", "libmpv-2.dll")]
    [InlineData(false, "ffmpeg", "ffprobe", "libmpv.so.2")]
    public void Discovers_complete_platform_payload(
        bool isWindows,
        string ffmpegName,
        string ffprobeName,
        string libMpvName)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var toolDirectory = Directory.CreateDirectory(Path.Combine(root, "tools", "ffmpeg"));
            var ffmpegPath = WriteEmptyFile(toolDirectory.FullName, ffmpegName);
            var ffprobePath = WriteEmptyFile(toolDirectory.FullName, ffprobeName);
            var libMpvPath = WriteEmptyFile(root, libMpvName);

            var layout = BundledRuntimeBootstrapper.Discover(root, isWindows);

            Assert.Equal(ffmpegPath, layout.FfmpegPath);
            Assert.Equal(ffprobePath, layout.FfprobePath);
            Assert.Equal(libMpvPath, layout.LibMpvPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_payload_files_are_not_advertised()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = BundledRuntimeBootstrapper.Discover(root, isWindows: true);

            Assert.Null(layout.FfmpegPath);
            Assert.Null(layout.FfprobePath);
            Assert.Null(layout.LibMpvPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ClipEdit.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteEmptyFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, []);
        return Path.GetFullPath(path);
    }
}
