using System.Diagnostics;
using System.Text.RegularExpressions;
using ClipEdit.Media.FFmpeg.Process;
using ClipEdit.Media.Mpv;

namespace ClipEdit.App.Settings;

internal enum MediaDependencyOrigin
{
    Unknown,
    Manual,
    System,
    Bundled,
}

internal sealed record MediaDependencyValidation(
    bool IsValid,
    string? ResolvedPath,
    string? Version,
    string? Error,
    MediaDependencyOrigin Origin = MediaDependencyOrigin.Unknown)
{
    public static MediaDependencyValidation Missing(
        string message,
        MediaDependencyOrigin origin = MediaDependencyOrigin.Unknown) =>
        new(false, null, null, message, origin);
}

internal sealed record MediaRuntimeValidation(
    MediaDependencyValidation Ffmpeg,
    MediaDependencyValidation Ffprobe,
    MediaDependencyValidation LibMpv);

internal sealed record MediaToolExecutionResult(
    bool IsSuccessful,
    string Output,
    string? Error);

internal sealed record LibMpvInspectionResult(
    bool IsValid,
    string? Version,
    string? Error);

internal sealed partial class MediaRuntimeValidator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<string, CancellationToken, Task<MediaToolExecutionResult>> _toolRunner;
    private readonly Func<string, LibMpvInspectionResult> _libMpvInspector;

    public MediaRuntimeValidator()
        : this(RunVersionCommandAsync, InspectLibMpv)
    {
    }

    internal MediaRuntimeValidator(
        Func<string, CancellationToken, Task<MediaToolExecutionResult>> toolRunner,
        Func<string, LibMpvInspectionResult> libMpvInspector)
    {
        _toolRunner = toolRunner;
        _libMpvInspector = libMpvInspector;
    }

    public async Task<MediaRuntimeValidation> ValidateAsync(
        MediaRuntimeSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();

        var ffmpegTask = ValidateExecutableAsync(
            "FFmpeg",
            "ffmpeg",
            settings.FfmpegPath,
            () => FfmpegToolLocator.FindFfmpeg(null, settings.PreferSystemMediaTools),
            () => FfmpegToolLocator.FindFfmpeg(null, !settings.PreferSystemMediaTools),
            cancellationToken);
        var ffprobeTask = ValidateExecutableAsync(
            "ffprobe",
            "ffprobe",
            settings.FfprobePath,
            () => FfmpegToolLocator.FindFfprobe(null, settings.PreferSystemMediaTools),
            () => FfmpegToolLocator.FindFfprobe(null, !settings.PreferSystemMediaTools),
            cancellationToken);
        var libMpv = ValidateLibMpv(settings);

        await Task.WhenAll(ffmpegTask, ffprobeTask).ConfigureAwait(false);
        return new MediaRuntimeValidation(
            await ffmpegTask.ConfigureAwait(false),
            await ffprobeTask.ConfigureAwait(false),
            libMpv);
    }

    private async Task<MediaDependencyValidation> ValidateExecutableAsync(
        string displayName,
        string toolName,
        string? manualPath,
        Func<string?> automaticResolver,
        Func<string?> alternativeResolver,
        CancellationToken cancellationToken)
    {
        var isManual = !string.IsNullOrWhiteSpace(manualPath);
        var resolvedPath = isManual
            ? ResolveFile(manualPath)
            : automaticResolver();
        if (resolvedPath is null)
        {
            return MediaDependencyValidation.Missing(
                isManual
                    ? $"The selected {displayName} file does not exist."
                    : $"{displayName} was not found.",
                isManual ? MediaDependencyOrigin.Manual : MediaDependencyOrigin.Unknown);
        }

        var validation = await InspectExecutableAsync(
            displayName,
            resolvedPath,
            isManual
                ? MediaDependencyOrigin.Manual
                : ClassifyExecutableOrigin(toolName, resolvedPath),
            cancellationToken).ConfigureAwait(false);
        if (validation.IsValid || isManual)
        {
            return validation;
        }

        var fallbackPath = alternativeResolver();
        if (fallbackPath is null || PathsEqual(fallbackPath, resolvedPath))
        {
            return validation;
        }

        return await InspectExecutableAsync(
            displayName,
            fallbackPath,
            ClassifyExecutableOrigin(toolName, fallbackPath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<MediaDependencyValidation> InspectExecutableAsync(
        string displayName,
        string path,
        MediaDependencyOrigin origin,
        CancellationToken cancellationToken)
    {
        var execution = await _toolRunner(path, cancellationToken).ConfigureAwait(false);
        if (!execution.IsSuccessful)
        {
            return new MediaDependencyValidation(
                false,
                path,
                null,
                execution.Error ?? $"{displayName} did not return version information.",
                origin);
        }

        var version = ParseVersion(execution.Output);
        return version is null
            ? new MediaDependencyValidation(
                false,
                path,
                null,
                $"The selected file does not identify itself as {displayName}.",
                origin)
            : new MediaDependencyValidation(true, path, version, null, origin);
    }

    private MediaDependencyValidation ValidateLibMpv(MediaRuntimeSettings settings)
    {
        var isManual = !string.IsNullOrWhiteSpace(settings.LibMpvPath);
        var resolvedPath = isManual
            ? ResolveLibMpv(settings.LibMpvPath)
            : MpvNativeLibraryLocator.Find(null, settings.PreferSystemMediaTools);
        if (resolvedPath is null)
        {
            return MediaDependencyValidation.Missing(
                isManual
                    ? "The selected libmpv file does not exist."
                    : "A compatible libmpv was not found.",
                isManual ? MediaDependencyOrigin.Manual : MediaDependencyOrigin.Unknown);
        }

        var inspection = _libMpvInspector(resolvedPath);
        var origin = isManual
            ? MediaDependencyOrigin.Manual
            : ClassifyLibMpvOrigin(resolvedPath);
        return inspection.IsValid
            ? new MediaDependencyValidation(true, resolvedPath, inspection.Version, null, origin)
            : new MediaDependencyValidation(false, resolvedPath, null, inspection.Error, origin);
    }

    private static string? ResolveFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ResolveLibMpv(string? path)
    {
        if (OperatingSystem.IsLinux() &&
            string.Equals(path?.Trim(), "libmpv.so.2", StringComparison.Ordinal))
        {
            return "libmpv.so.2";
        }

        return ResolveFile(path);
    }

    private static MediaDependencyOrigin ClassifyExecutableOrigin(string toolName, string path)
    {
        var executableName = OperatingSystem.IsWindows() ? $"{toolName}.exe" : toolName;
        var bundledVariable = toolName == "ffprobe"
            ? "CLIPEDIT_BUNDLED_FFPROBE_PATH"
            : "CLIPEDIT_BUNDLED_FFMPEG_PATH";
        return IsBundledPath(
            path,
            Environment.GetEnvironmentVariable(bundledVariable),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", executableName))
            ? MediaDependencyOrigin.Bundled
            : MediaDependencyOrigin.System;
    }

    private static MediaDependencyOrigin ClassifyLibMpvOrigin(string path)
    {
        var libraryName = OperatingSystem.IsWindows() ? "libmpv-2.dll" : "libmpv.so.2";
        return IsBundledPath(
            path,
            Environment.GetEnvironmentVariable("CLIPEDIT_BUNDLED_LIBMPV_PATH"),
            Path.Combine(AppContext.BaseDirectory, libraryName))
            ? MediaDependencyOrigin.Bundled
            : MediaDependencyOrigin.System;
    }

    private static bool IsBundledPath(string path, string? advertisedPath, string baseDirectoryPath)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        if (PathsEqual(path, baseDirectoryPath) ||
            (!string.IsNullOrWhiteSpace(advertisedPath) && PathsEqual(path, advertisedPath)))
        {
            return true;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var nativePackageMarker = string.Join(
            Path.DirectorySeparatorChar,
            string.Empty,
            "packages",
            "native",
            string.Empty);
        return normalized.Contains(
            nativePackageMarker,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    internal static string? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static LibMpvInspectionResult InspectLibMpv(string path)
    {
        return MpvNativeLibraryLocator.TryGetClientApiVersion(path, out var version, out var error)
            ? new LibMpvInspectionResult(true, $"client API {version}", null)
            : new LibMpvInspectionResult(false, null, error ?? "libmpv could not be loaded.");
    }

    private static async Task<MediaToolExecutionResult> RunVersionCommandAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-version");

        try
        {
            if (!process.Start())
            {
                return new MediaToolExecutionResult(false, string.Empty, "The process could not start.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            var combined = string.Join(
                Environment.NewLine,
                new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return process.ExitCode == 0
                ? new MediaToolExecutionResult(true, combined, null)
                : new MediaToolExecutionResult(
                    false,
                    combined,
                    $"The version check exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new MediaToolExecutionResult(false, string.Empty, "The version check timed out.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new MediaToolExecutionResult(false, string.Empty, exception.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    [GeneratedRegex(@"(?im)^ff(?:mpeg|probe) version\s+([^\s]+)")]
    private static partial Regex VersionPattern();
}
