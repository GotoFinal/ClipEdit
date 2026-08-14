using ClipEdit.Media.Mpv;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvNativeLibraryLocatorTests
{
    [Fact]
    public void Missing_library_fails_client_api_inspection_with_actionable_error()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"missing-libmpv-{Guid.NewGuid():N}",
            OperatingSystem.IsWindows() ? "libmpv-2.dll" : "libmpv.so.2");

        Assert.False(MpvNativeLibraryLocator.TryGetClientApiVersion(
            missingPath,
            out _,
            out var error));
        Assert.Contains("Could not load libmpv", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_library_is_preferred_by_default_and_system_library_is_the_fallback()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var bundled = WriteLibrary(directory, "bundled-libmpv.so.2");

            Assert.Equal(
                bundled,
                MpvNativeLibraryLocator.FindCore(
                    null,
                    null,
                    [bundled],
                    "libmpv.so.2",
                    preferSystem: false,
                    static name => name));
            File.Delete(bundled);
            Assert.Equal(
                "libmpv.so.2",
                MpvNativeLibraryLocator.FindCore(
                    null,
                    null,
                    [bundled],
                    "libmpv.so.2",
                    preferSystem: false,
                    static name => name));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void System_library_is_preferred_when_requested_but_manual_path_remains_authoritative()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var manual = WriteLibrary(directory, "manual-libmpv.so.2");
            var bundled = WriteLibrary(directory, "bundled-libmpv.so.2");

            Assert.Equal(
                "libmpv.so.2",
                MpvNativeLibraryLocator.FindCore(
                    null,
                    null,
                    [bundled],
                    "libmpv.so.2",
                    preferSystem: true,
                    static name => name));
            Assert.Equal(
                manual,
                MpvNativeLibraryLocator.FindCore(
                    manual,
                    null,
                    [bundled],
                    "libmpv.so.2",
                    preferSystem: true,
                    static name => name));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Incompatible_system_library_falls_back_to_bundled_library()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var bundled = WriteLibrary(directory, "bundled-libmpv.so.2");

            Assert.Equal(
                bundled,
                MpvNativeLibraryLocator.FindCore(
                    null,
                    null,
                    [bundled],
                    "libmpv.so.2",
                    preferSystem: true,
                    static _ => null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ClipEdit.Mpv.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteLibrary(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, []);
        return Path.GetFullPath(path);
    }
}
