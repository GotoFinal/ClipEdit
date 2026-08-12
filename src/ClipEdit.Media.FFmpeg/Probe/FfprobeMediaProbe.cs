using System.Diagnostics;
using System.Text;
using ClipEdit.Media.Probe;
using DiagnosticProcess = System.Diagnostics.Process;

namespace ClipEdit.Media.FFmpeg.Probe;

public sealed class FfprobeMediaProbe : IMediaProbe
{
    private const int MaximumStandardOutputCharacters = 4 * 1024 * 1024;
    private const int MaximumStandardErrorCharacters = 256 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly string _executablePath;
    private readonly TimeSpan _timeout;

    public FfprobeMediaProbe(string executablePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _executablePath = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(_executablePath) || !File.Exists(_executablePath))
        {
            throw new MediaProbeException(
                MediaProbeFailure.ToolUnavailable,
                "The configured ffprobe executable does not exist.");
        }

        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<MediaProbeResult> ProbeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!Path.IsPathFullyQualified(fullSourcePath) || !File.Exists(fullSourcePath))
        {
            throw new MediaProbeException(
                MediaProbeFailure.SourceUnavailable,
                "The selected media file no longer exists or cannot be accessed.");
        }

        using var process = new DiagnosticProcess
        {
            StartInfo = CreateStartInfo(fullSourcePath),
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                throw new MediaProbeException(
                    MediaProbeFailure.ToolUnavailable,
                    "ffprobe could not be started.");
            }
        }
        catch (Exception exception) when (exception is not MediaProbeException)
        {
            throw new MediaProbeException(
                MediaProbeFailure.ToolUnavailable,
                "ffprobe could not be started.",
                exception);
        }

        using var timeoutCancellation = new CancellationTokenSource(_timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        var standardOutputTask = ReadBoundedAsync(
            process.StandardOutput,
            MaximumStandardOutputCharacters,
            linkedCancellation.Token);
        var standardErrorTask = ReadBoundedAsync(
            process.StandardError,
            MaximumStandardErrorCharacters,
            linkedCancellation.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            var standardOutput = standardOutputTask.Result;
            var standardError = standardErrorTask.Result;

            if (process.ExitCode != 0)
            {
                throw new MediaProbeException(
                    MediaProbeFailure.ToolFailed,
                    BuildToolFailureMessage(process.ExitCode, standardError));
            }

            return FfprobeJsonParser.Parse(fullSourcePath, standardOutput);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new MediaProbeException(
                MediaProbeFailure.TimedOut,
                $"ffprobe did not finish within {_timeout.TotalSeconds:0} seconds.");
        }
        catch (OutputLimitExceededException exception)
        {
            TryKill(process);
            throw new MediaProbeException(
                MediaProbeFailure.OutputTooLarge,
                "ffprobe returned more metadata than ClipEdit accepts.",
                exception);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(string sourcePath)
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

        foreach (var argument in FfprobeArguments.Create(sourcePath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(maximumCharacters, 64 * 1024));
        var exceeded = false;

        while (true)
        {
            var read = await reader
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (!exceeded && output.Length + read <= maximumCharacters)
            {
                output.Append(buffer, 0, read);
            }
            else
            {
                exceeded = true;
            }
        }

        if (exceeded)
        {
            throw new OutputLimitExceededException();
        }

        return output.ToString();
    }

    private static string BuildToolFailureMessage(int exitCode, string standardError)
    {
        var detail = standardError.Trim();
        if (detail.Length > 500)
        {
            detail = detail[..500] + "…";
        }

        return string.IsNullOrEmpty(detail)
            ? $"ffprobe failed with exit code {exitCode}."
            : $"ffprobe failed with exit code {exitCode}: {detail}";
    }

    private static void TryKill(DiagnosticProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and kill request.
        }
    }

    private sealed class OutputLimitExceededException : Exception;
}
