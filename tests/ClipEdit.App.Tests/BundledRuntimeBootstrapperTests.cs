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

    [Fact]
    public void Bundled_payload_sets_defaults_without_overriding_explicit_paths()
    {
        var root = CreateTemporaryDirectory();
        var variables = new[]
        {
            BundledRuntimeBootstrapper.FfmpegEnvironmentVariable,
            BundledRuntimeBootstrapper.FfprobeEnvironmentVariable,
            BundledRuntimeBootstrapper.LibMpvEnvironmentVariable,
        };
        var priorValues = variables.ToDictionary(
            variable => variable,
            Environment.GetEnvironmentVariable);
        try
        {
            var toolDirectory = Directory.CreateDirectory(Path.Combine(root, "tools", "ffmpeg"));
            var ffmpegPath = WriteEmptyFile(toolDirectory.FullName, "ffmpeg.exe");
            var ffprobePath = WriteEmptyFile(toolDirectory.FullName, "ffprobe.exe");
            _ = WriteEmptyFile(root, "libmpv-2.dll");
            const string explicitLibMpvPath = "explicit-libmpv.dll";
            Environment.SetEnvironmentVariable(BundledRuntimeBootstrapper.FfmpegEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(BundledRuntimeBootstrapper.FfprobeEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(
                BundledRuntimeBootstrapper.LibMpvEnvironmentVariable,
                explicitLibMpvPath);

            BundledRuntimeBootstrapper.Prepare(root, isWindows: true, isLinux: false);

            Assert.Equal(
                ffmpegPath,
                Environment.GetEnvironmentVariable(BundledRuntimeBootstrapper.FfmpegEnvironmentVariable));
            Assert.Equal(
                ffprobePath,
                Environment.GetEnvironmentVariable(BundledRuntimeBootstrapper.FfprobeEnvironmentVariable));
            Assert.Equal(
                explicitLibMpvPath,
                Environment.GetEnvironmentVariable(BundledRuntimeBootstrapper.LibMpvEnvironmentVariable));
        }
        finally
        {
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable, priorValues[variable]);
            }

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
