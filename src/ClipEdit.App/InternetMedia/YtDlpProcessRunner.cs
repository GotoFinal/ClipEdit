using System.Diagnostics;
using System.Text;
using DiagnosticProcess = System.Diagnostics.Process;

namespace ClipEdit.App.InternetMedia;

internal sealed record YtDlpProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IYtDlpProcessRunner
{
    Task<YtDlpProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool captureStandardOutput,
        Action<string>? standardOutputLine,
        CancellationToken cancellationToken);
}

internal sealed class YtDlpProcessRunner : IYtDlpProcessRunner
{
    private const int MaximumCapturedOutputCharacters = 16 * 1024 * 1024;
    private const int MaximumDiagnosticCharacters = 256 * 1024;

    public async Task<YtDlpProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool captureStandardOutput,
        Action<string>? standardOutputLine,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new DiagnosticProcess { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InternetMediaException("yt-dlp could not be started.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InternetMediaException("yt-dlp could not be started.", exception);
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputTask = ReadLinesAsync(
            process.StandardOutput,
            captureStandardOutput ? output : null,
            captureStandardOutput ? MaximumCapturedOutputCharacters : 0,
            standardOutputLine,
            cancellationToken);
        var errorTask = ReadLinesAsync(
            process.StandardError,
            error,
            MaximumDiagnosticCharacters,
            null,
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }

        return new YtDlpProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder? capture,
        int maximumCharacters,
        Action<string>? lineCallback,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineCallback?.Invoke(line);
            if (capture is null)
            {
                continue;
            }
            if (capture.Length + line.Length + Environment.NewLine.Length > maximumCharacters)
            {
                throw new InternetMediaException("yt-dlp returned more output than the safety limit.");
            }
            capture.AppendLine(line);
        }
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
