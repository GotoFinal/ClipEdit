using System.Diagnostics;
using System.Text;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Frames;
using DiagnosticProcess = System.Diagnostics.Process;

namespace ClipEdit.Media.FFmpeg.Frames;

public sealed class FfmpegFrameDecoder : IFrameDecoder
{
    private const int MaximumImageBytes = 32 * 1024 * 1024;
    private const int MaximumErrorCharacters = 256 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly string _executablePath;
    private readonly TimeSpan _timeout;

    public FfmpegFrameDecoder(string executablePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(_executablePath))
        {
            throw new FrameDecodeException(
                FrameDecodeFailure.ToolUnavailable,
                "The configured FFmpeg executable does not exist.");
        }

        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<DecodedFrame> DecodeAsync(
        string sourcePath,
        int videoStreamIndex,
        MediaTime timestamp,
        PixelSize maximumSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FrameDecodeException(
                FrameDecodeFailure.SourceUnavailable,
                "The media source no longer exists or cannot be accessed.");
        }

        using var process = new DiagnosticProcess
        {
            StartInfo = CreateStartInfo(
                fullSourcePath,
                videoStreamIndex,
                timestamp,
                maximumSize),
        };

        try
        {
            if (!process.Start())
            {
                throw new FrameDecodeException(
                    FrameDecodeFailure.ToolUnavailable,
                    "FFmpeg could not be started.");
            }
        }
        catch (Exception exception) when (exception is not FrameDecodeException)
        {
            throw new FrameDecodeException(
                FrameDecodeFailure.ToolUnavailable,
                "FFmpeg could not be started.",
                exception);
        }

        using var timeoutCancellation = new CancellationTokenSource(_timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        var imageTask = ReadBytesBoundedAsync(
            process.StandardOutput.BaseStream,
            MaximumImageBytes,
            linkedCancellation.Token);
        var errorTask = ReadTextBoundedAsync(
            process.StandardError,
            MaximumErrorCharacters,
            linkedCancellation.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            await Task.WhenAll(imageTask, errorTask).ConfigureAwait(false);
            var imageBytes = imageTask.Result;
            var errorText = errorTask.Result;

            if (process.ExitCode != 0)
            {
                throw new FrameDecodeException(
                    FrameDecodeFailure.ToolFailed,
                    BuildFailureMessage(process.ExitCode, errorText));
            }

            if (imageBytes.Length == 0)
            {
                throw new FrameDecodeException(
                    FrameDecodeFailure.NoFrame,
                    "FFmpeg did not return a video frame at the requested time.");
            }

            return new DecodedFrame(imageBytes, "image/png");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new FrameDecodeException(
                FrameDecodeFailure.TimedOut,
                $"Frame decoding did not finish within {_timeout.TotalSeconds:0} seconds.");
        }
        catch (OutputLimitExceededException exception)
        {
            TryKill(process);
            throw new FrameDecodeException(
                FrameDecodeFailure.OutputTooLarge,
                "The decoded preview image exceeded the configured safety limit.",
                exception);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(
        string sourcePath,
        int videoStreamIndex,
        MediaTime timestamp,
        PixelSize maximumSize)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in FfmpegFrameArguments.Create(
                     sourcePath,
                     videoStreamIndex,
                     timestamp,
                     maximumSize))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<byte[]> ReadBytesBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 512 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new OutputLimitExceededException();
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadTextBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(maximumCharacters, 32 * 1024));
        var exceeded = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (exceeded)
                {
                    throw new OutputLimitExceededException();
                }

                return output.ToString();
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
    }

    private static string BuildFailureMessage(int exitCode, string errorText)
    {
        var detail = errorText.Trim();
        if (detail.Length > 500)
        {
            detail = detail[..500] + "…";
        }

        return string.IsNullOrEmpty(detail)
            ? $"FFmpeg frame decoding failed with exit code {exitCode}."
            : $"FFmpeg frame decoding failed with exit code {exitCode}: {detail}";
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
            // The process exited between the state check and the kill request.
        }
    }

    private sealed class OutputLimitExceededException : Exception;
}
