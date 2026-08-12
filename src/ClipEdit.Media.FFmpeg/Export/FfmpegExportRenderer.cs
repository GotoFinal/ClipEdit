using System.Diagnostics;
using System.Text;
using ClipEdit.Media.Export;
using DiagnosticProcess = System.Diagnostics.Process;

namespace ClipEdit.Media.FFmpeg.Export;

public sealed class FfmpegExportRenderer : IExportRenderer
{
    private const int MaximumDiagnosticCharacters = 256 * 1024;
    private readonly string _executablePath;

    public FfmpegExportRenderer(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(_executablePath))
        {
            throw new ExportException(
                ExportFailure.ToolUnavailable,
                "The configured FFmpeg executable does not exist.");
        }
    }

    public async Task<ExportResult> RenderAsync(
        ExportPlan plan,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePaths(plan);

        var temporaryPath = CreateTemporaryPath(plan.DestinationPath);
        using var process = new DiagnosticProcess
        {
            StartInfo = CreateStartInfo(plan, temporaryPath),
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            StartProcess(process);
            progress?.Report(new ExportProgress(0, "Encoding", TimeSpan.Zero));

            var progressTask = ReadProgressAsync(process.StandardOutput, plan, progress);
            var diagnosticTask = ReadDiagnosticTailAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await WaitForExitWithoutCancellationAsync(process).ConfigureAwait(false);
                await IgnoreFailureAsync(progressTask).ConfigureAwait(false);
                await IgnoreFailureAsync(diagnosticTask).ConfigureAwait(false);
                TryDelete(temporaryPath);
                throw;
            }

            await progressTask.ConfigureAwait(false);
            var diagnostics = await diagnosticTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                TryDelete(temporaryPath);
                throw new ExportException(
                    ExportFailure.ToolFailed,
                    BuildFailureMessage(process.ExitCode, diagnostics));
            }

            progress?.Report(new ExportProgress(0.99, "Finalizing", plan.ExpectedDurationToTimeSpan()));
            var fileInfo = new FileInfo(temporaryPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                TryDelete(temporaryPath);
                throw new ExportException(
                    ExportFailure.EmptyOutput,
                    "FFmpeg completed without producing a usable output file.");
            }

            try
            {
                FinalizeOutput(temporaryPath, plan);
            }
            catch (IOException exception)
            {
                TryDelete(temporaryPath);
                throw new ExportException(
                    ExportFailure.DestinationUnavailable,
                    "The completed export could not be finalized at the selected destination.",
                    exception);
            }

            stopwatch.Stop();
            progress?.Report(new ExportProgress(1, "Complete", plan.ExpectedDurationToTimeSpan()));
            return new ExportResult(plan.DestinationPath, fileInfo.Length, stopwatch.Elapsed);
        }
        catch
        {
            TryKill(process);
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void ValidatePaths(ExportPlan plan)
    {
        if (!File.Exists(plan.SourcePath))
        {
            throw new ExportException(
                ExportFailure.SourceUnavailable,
                "The source media no longer exists or cannot be accessed.");
        }

        if (plan.AudioTracks.Any(track =>
                track.ExternalSourcePath is not null && !File.Exists(track.ExternalSourcePath)))
        {
            throw new ExportException(
                ExportFailure.SourceUnavailable,
                "An external audio source no longer exists or cannot be accessed.");
        }

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(plan.SourcePath, plan.DestinationPath, pathComparison))
        {
            throw new ExportException(
                ExportFailure.DestinationUnavailable,
                "The export destination cannot replace the source media.");
        }

        if (File.Exists(plan.DestinationPath) && !plan.ReplaceExistingDestination)
        {
            throw new ExportException(
                ExportFailure.DestinationExists,
                "The export destination already exists. Choose a new name or confirm replacement first.");
        }

        var directory = Path.GetDirectoryName(plan.DestinationPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new ExportException(
                ExportFailure.DestinationUnavailable,
                "The selected export folder does not exist or cannot be accessed.");
        }
    }

    internal static void FinalizeOutput(string temporaryPath, ExportPlan plan)
    {
        if (!File.Exists(plan.DestinationPath))
        {
            File.Move(temporaryPath, plan.DestinationPath, overwrite: false);
            return;
        }

        if (!plan.ReplaceExistingDestination)
        {
            throw new IOException("The export destination appeared before finalization.");
        }

        var directory = Path.GetDirectoryName(plan.DestinationPath)!;
        var backupPath = Path.Combine(
            directory,
            $".{Path.GetFileName(plan.DestinationPath)}.{Guid.NewGuid():N}.backup");
        File.Move(plan.DestinationPath, backupPath, overwrite: false);
        try
        {
            File.Move(temporaryPath, plan.DestinationPath, overwrite: false);
            TryDelete(backupPath);
        }
        catch
        {
            if (!File.Exists(plan.DestinationPath) && File.Exists(backupPath))
            {
                File.Move(backupPath, plan.DestinationPath, overwrite: false);
            }

            throw;
        }
    }

    private static string CreateTemporaryPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)!;
        var fileName = Path.GetFileName(destinationPath);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.partial");
    }

    private ProcessStartInfo CreateStartInfo(ExportPlan plan, string temporaryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in FfmpegExportArguments.Create(plan, temporaryPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void StartProcess(DiagnosticProcess process)
    {
        try
        {
            if (!process.Start())
            {
                throw new ExportException(
                    ExportFailure.ToolUnavailable,
                    "FFmpeg could not be started.");
            }
        }
        catch (Exception exception) when (exception is not ExportException)
        {
            throw new ExportException(
                ExportFailure.ToolUnavailable,
                "FFmpeg could not be started.",
                exception);
        }
    }

    private static async Task ReadProgressAsync(
        StreamReader reader,
        ExportPlan plan,
        IProgress<ExportProgress>? progress)
    {
        var parser = new FfmpegProgressParser();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (!parser.Parse(line))
            {
                continue;
            }

            var expectedSeconds = plan.ExpectedDuration.TotalSeconds;
            var fraction = expectedSeconds <= 0
                ? 0
                : Math.Min(0.98, parser.EncodedDuration.TotalSeconds / expectedSeconds);
            progress?.Report(new ExportProgress(fraction, "Encoding", parser.EncodedDuration));
        }
    }

    private static async Task<string> ReadDiagnosticTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                return tail.ToString();
            }

            tail.Append(buffer, 0, read);
            if (tail.Length > MaximumDiagnosticCharacters)
            {
                tail.Remove(0, tail.Length - MaximumDiagnosticCharacters);
            }
        }
    }

    private static string BuildFailureMessage(int exitCode, string diagnostics)
    {
        var detail = diagnostics.Trim();
        if (detail.Length > 1_000)
        {
            detail = detail[^1_000..];
        }

        return string.IsNullOrEmpty(detail)
            ? $"FFmpeg export failed with exit code {exitCode}."
            : $"FFmpeg export failed with exit code {exitCode}: {detail}";
    }

    private static async Task WaitForExitWithoutCancellationAsync(DiagnosticProcess process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process never started or was already disposed.
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancellation cleanup should preserve the original cancellation.
        }
    }

    private static void TryKill(DiagnosticProcess process)
    {
        try
        {
            if (process.StartInfo is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process was not started or exited between checks.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A stale private partial is safer than touching an existing destination.
        }
        catch (UnauthorizedAccessException)
        {
            // A stale private partial is safer than touching an existing destination.
        }
    }
}

file static class ExportPlanTimeExtensions
{
    public static TimeSpan ExpectedDurationToTimeSpan(this ExportPlan plan)
    {
        return TimeSpan.FromSeconds(plan.ExpectedDuration.TotalSeconds);
    }
}
