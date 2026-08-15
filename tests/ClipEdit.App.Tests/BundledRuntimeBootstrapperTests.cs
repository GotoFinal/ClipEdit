using ClipEdit.App;
using System.IO.Compression;

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
            var libMpvPath = WriteEmptyFile(
                isWindows ? toolDirectory.FullName : root,
                libMpvName);

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
    public void Windows_development_layout_keeps_root_libmpv_fallback()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var toolDirectory = Directory.CreateDirectory(Path.Combine(root, "tools", "ffmpeg"));
            _ = WriteEmptyFile(toolDirectory.FullName, "ffmpeg.exe");
            _ = WriteEmptyFile(toolDirectory.FullName, "ffprobe.exe");
            var libMpvPath = WriteEmptyFile(root, "libmpv-2.dll");

            var layout = BundledRuntimeBootstrapper.Discover(root, isWindows: true);

            Assert.Equal(libMpvPath, layout.LibMpvPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Bundled_payload_paths_are_advertised_for_media_locators()
    {
        var root = CreateTemporaryDirectory();
        var variables = new[]
        {
            BundledRuntimeBootstrapper.BundledFfmpegEnvironmentVariable,
            BundledRuntimeBootstrapper.BundledFfprobeEnvironmentVariable,
            BundledRuntimeBootstrapper.BundledLibMpvEnvironmentVariable,
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
            Environment.SetEnvironmentVariable(
                BundledRuntimeBootstrapper.BundledFfmpegEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                BundledRuntimeBootstrapper.BundledFfprobeEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                BundledRuntimeBootstrapper.BundledLibMpvEnvironmentVariable,
                null);

            BundledRuntimeBootstrapper.Prepare(root, isWindows: true, isLinux: false);

            Assert.Equal(
                ffmpegPath,
                Environment.GetEnvironmentVariable(
                    BundledRuntimeBootstrapper.BundledFfmpegEnvironmentVariable));
            Assert.Equal(
                ffprobePath,
                Environment.GetEnvironmentVariable(
                    BundledRuntimeBootstrapper.BundledFfprobeEnvironmentVariable));
            Assert.Equal(
                Path.Combine(root, "libmpv-2.dll"),
                Environment.GetEnvironmentVariable(
                    BundledRuntimeBootstrapper.BundledLibMpvEnvironmentVariable));
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

    [Fact]
    public void Embedded_archive_is_extracted_once_and_reused_from_cache()
    {
        var root = CreateTemporaryDirectory();
        var destination = Path.Combine(root, "runtime", "test-id");
        var archiveBytes = CreateArchive(
            ("tools/ffmpeg/ffmpeg.exe", "ffmpeg"),
            ("tools/ffmpeg/ffprobe.exe", "ffprobe"),
            ("tools/ffmpeg/libmpv-2.dll", "mpv"));
        var opened = 0;
        bool IsComplete(string candidate) =>
            File.Exists(Path.Combine(candidate, "tools", "ffmpeg", "ffmpeg.exe")) &&
            File.Exists(Path.Combine(candidate, "tools", "ffmpeg", "ffprobe.exe")) &&
            File.Exists(Path.Combine(candidate, "tools", "ffmpeg", "libmpv-2.dll"));

        try
        {
            Stream OpenArchive()
            {
                opened++;
                return new MemoryStream(archiveBytes, writable: false);
            }

            var extracted = BundledRuntimeBootstrapper.ExtractArchiveToCache(
                OpenArchive,
                "test archive",
                "test-id",
                destination,
                IsComplete);
            var reused = BundledRuntimeBootstrapper.ExtractArchiveToCache(
                OpenArchive,
                "test archive",
                "test-id",
                destination,
                IsComplete);

            Assert.True(extracted);
            Assert.False(reused);
            Assert.Equal(1, opened);
            Assert.Equal("ffmpeg", File.ReadAllText(
                Path.Combine(destination, "tools", "ffmpeg", "ffmpeg.exe")));
            Assert.True(File.Exists(Path.Combine(destination, ".complete")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Complete_cached_runtime_is_advertised_before_window_startup()
    {
        const string mediaId = "cached-runtime-test";
        var root = CreateTemporaryDirectory();
        var runtime = Path.Combine(root, "Runtime", mediaId);
        var variables = new[]
        {
            BundledRuntimeBootstrapper.BundledFfmpegEnvironmentVariable,
            BundledRuntimeBootstrapper.BundledFfprobeEnvironmentVariable,
            BundledRuntimeBootstrapper.BundledLibMpvEnvironmentVariable,
        };
        var priorValues = variables.ToDictionary(
            variable => variable,
            Environment.GetEnvironmentVariable);
        try
        {
            var toolDirectory = Directory.CreateDirectory(Path.Combine(runtime, "tools", "ffmpeg"));
            var ffmpegPath = WriteEmptyFile(toolDirectory.FullName, "ffmpeg.exe");
            var ffprobePath = WriteEmptyFile(toolDirectory.FullName, "ffprobe.exe");
            var libMpvPath = WriteEmptyFile(toolDirectory.FullName, "libmpv-2.dll");
            File.WriteAllText(Path.Combine(runtime, ".complete"), mediaId);
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }

            var layout = BundledRuntimeBootstrapper.PrepareCachedMediaRuntime(
                root,
                mediaId,
                isWindows: true,
                isLinux: false);

            Assert.NotNull(layout);
            Assert.Equal(ffmpegPath, layout.FfmpegPath);
            Assert.Equal(ffprobePath, layout.FfprobePath);
            Assert.Equal(libMpvPath, layout.LibMpvPath);
            Assert.Equal(
                libMpvPath,
                Environment.GetEnvironmentVariable(
                    BundledRuntimeBootstrapper.BundledLibMpvEnvironmentVariable));
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

    [Fact]
    public void Embedded_archive_rejects_paths_outside_its_cache_directory()
    {
        var root = CreateTemporaryDirectory();
        var destination = Path.Combine(root, "runtime");
        var archiveBytes = CreateArchive(("../escaped.txt", "bad"));
        try
        {
            using var archive = new MemoryStream(archiveBytes, writable: false);

            Assert.Throws<InvalidDataException>(() =>
                BundledRuntimeBootstrapper.ExtractZipArchive(archive, destination));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
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

    private static byte[] CreateArchive(params (string Path, string Contents)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, contents) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(contents);
            }
        }
        return output.ToArray();
    }
}
