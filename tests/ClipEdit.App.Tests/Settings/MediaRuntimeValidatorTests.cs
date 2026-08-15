using ClipEdit.App.Settings;
using ClipEdit.App.ViewModels;

namespace ClipEdit.App.Tests.Settings;

public sealed class MediaRuntimeValidatorTests
{
    [Theory]
    [InlineData("ffmpeg version 9.0.1-full_build", "9.0.1-full_build")]
    [InlineData("ffprobe version n7.1 Copyright", "n7.1")]
    [InlineData("prefix\nFFmpeg version N-12345-gabcdef", "N-12345-gabcdef")]
    public void Version_output_is_parsed_without_including_build_banner(
        string output,
        string expected)
    {
        Assert.Equal(expected, MediaRuntimeValidator.ParseVersion(output));
    }

    [Fact]
    public async Task Manual_dependencies_are_executed_and_report_detected_versions()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var ffmpeg = WriteEmptyFile(directory, "ffmpeg-test");
            var ffprobe = WriteEmptyFile(directory, "ffprobe-test");
            var libMpv = WriteEmptyFile(directory, "libmpv-test");
            var validator = new MediaRuntimeValidator(
                (path, _) => Task.FromResult(new MediaToolExecutionResult(
                    true,
                    path == ffmpeg
                        ? "ffmpeg version 9.0.1"
                        : "ffprobe version 9.0.1",
                    null)),
                _ => new LibMpvInspectionResult(true, "client API 2.5", null));

            var result = await validator.ValidateAsync(
                new MediaRuntimeSettings(true, ffmpeg, ffprobe, libMpv));

            Assert.Equal(
                new MediaDependencyValidation(
                    true,
                    ffmpeg,
                    "9.0.1",
                    null,
                    MediaDependencyOrigin.Manual),
                result.Ffmpeg);
            Assert.Equal(
                new MediaDependencyValidation(
                    true,
                    ffprobe,
                    "9.0.1",
                    null,
                    MediaDependencyOrigin.Manual),
                result.Ffprobe);
            Assert.Equal(
                new MediaDependencyValidation(
                    true,
                    libMpv,
                    "client API 2.5",
                    null,
                    MediaDependencyOrigin.Manual),
                result.LibMpv);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_manual_paths_are_invalid_instead_of_silently_showing_a_fallback()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
        var validator = new MediaRuntimeValidator(
            (_, _) => throw new InvalidOperationException("The process runner must not be called."),
            _ => throw new InvalidOperationException("The libmpv inspector must not be called."));

        var result = await validator.ValidateAsync(
            new MediaRuntimeSettings(
                false,
                Path.Combine(missingRoot, "ffmpeg"),
                Path.Combine(missingRoot, "ffprobe"),
                Path.Combine(missingRoot, "libmpv")));

        Assert.False(result.Ffmpeg.IsValid);
        Assert.False(result.Ffprobe.IsValid);
        Assert.False(result.LibMpv.IsValid);
        Assert.Contains("does not exist", result.Ffmpeg.Error, StringComparison.Ordinal);
        Assert.Contains("does not exist", result.Ffprobe.Error, StringComparison.Ordinal);
        Assert.Contains("does not exist", result.LibMpv.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validated_ffmpeg_and_ffprobe_are_applied_live_while_libmpv_requests_restart()
    {
        var directory = CreateTemporaryDirectory();
        var viewModel = new MainWindowViewModel(mediaProbe: null);
        try
        {
            var ffmpeg = WriteEmptyFile(directory, "ffmpeg-test");
            var ffprobe = WriteEmptyFile(directory, "ffprobe-test");
            var libMpv = WriteEmptyFile(directory, "libmpv-test");

            viewModel.ApplyMediaRuntimeValidation(new MediaRuntimeValidation(
                new MediaDependencyValidation(
                    true,
                    ffmpeg,
                    "9.0.1",
                    null,
                    MediaDependencyOrigin.System),
                new MediaDependencyValidation(
                    true,
                    ffprobe,
                    "9.0.1",
                    null,
                    MediaDependencyOrigin.Bundled),
                new MediaDependencyValidation(
                    true,
                    libMpv,
                    "client API 2.5",
                    null,
                    MediaDependencyOrigin.Manual)));

            Assert.True(viewModel.IsExportAvailable);
            Assert.True(viewModel.IsImportAvailable);
            Assert.True(viewModel.FfmpegRuntimeStatus.IsValid);
            Assert.True(viewModel.FfprobeRuntimeStatus.IsValid);
            Assert.True(viewModel.LibMpvRuntimeStatus.IsValid);
            Assert.Equal("System · 9.0.1", viewModel.FfmpegRuntimeStatus.Text);
            Assert.Equal("Bundled · 9.0.1", viewModel.FfprobeRuntimeStatus.Text);
            Assert.StartsWith("Manual · client API 2.5", viewModel.LibMpvRuntimeStatus.Text);
            Assert.Contains("restart to apply", viewModel.LibMpvRuntimeStatus.Text, StringComparison.Ordinal);
        }
        finally
        {
            viewModel.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task View_model_runs_dependency_inspection_away_from_the_calling_thread()
    {
        var directory = CreateTemporaryDirectory();
        var viewModel = new MainWindowViewModel(mediaProbe: null);
        try
        {
            var ffmpeg = WriteEmptyFile(directory, "ffmpeg-test");
            var ffprobe = WriteEmptyFile(directory, "ffprobe-test");
            var libMpv = WriteEmptyFile(directory, "libmpv-test");
            var callingThread = Environment.CurrentManagedThreadId;
            var inspectionThread = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var validator = new MediaRuntimeValidator(
                (path, _) => Task.FromResult(new MediaToolExecutionResult(
                    true,
                    path == ffmpeg
                        ? "ffmpeg version 9.0.1"
                        : "ffprobe version 9.0.1",
                    null)),
                _ =>
                {
                    inspectionThread.TrySetResult(Environment.CurrentManagedThreadId);
                    return new LibMpvInspectionResult(true, "client API 2.5", null);
                });

            viewModel.ConfigureMediaRuntime(
                new MediaRuntimeSettings(false, ffmpeg, ffprobe, libMpv),
                ffmpeg,
                ffprobe,
                libMpv,
                validator);

            var workerThread = await inspectionThread.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(callingThread, workerThread);
        }
        finally
        {
            viewModel.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-media-validator-{Guid.NewGuid():N}");
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
