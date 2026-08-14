using ClipEdit.Media.FFmpeg.Process;

namespace ClipEdit.Media.FFmpeg.Tests.Process;

public sealed class FfmpegToolLocatorTests
{
    [Fact]
    public void Bundled_tool_is_preferred_by_default_and_system_tool_is_the_fallback()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var bundled = WriteTool(directory, "bundled-ffmpeg");
            var system = WriteTool(directory, "system-ffmpeg");

            Assert.Equal(
                bundled,
                FfmpegToolLocator.FindCore(null, null, [bundled], [system], preferSystem: false));
            File.Delete(bundled);
            Assert.Equal(
                system,
                FfmpegToolLocator.FindCore(null, null, [bundled], [system], preferSystem: false));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void System_tool_is_preferred_when_requested_but_manual_path_remains_authoritative()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var manual = WriteTool(directory, "manual-ffmpeg");
            var bundled = WriteTool(directory, "bundled-ffmpeg");
            var system = WriteTool(directory, "system-ffmpeg");

            Assert.Equal(
                system,
                FfmpegToolLocator.FindCore(null, null, [bundled], [system], preferSystem: true));
            Assert.Equal(
                manual,
                FfmpegToolLocator.FindCore(manual, null, [bundled], [system], preferSystem: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ClipEdit.FFmpeg.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteTool(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, []);
        return Path.GetFullPath(path);
    }
}
