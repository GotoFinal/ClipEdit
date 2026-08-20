using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ClipEdit.Media.Export;
using DiagnosticProcess = System.Diagnostics.Process;

namespace ClipEdit.Media.FFmpeg.Export;

public sealed class FfmpegHardwareCapabilityProbe : IExportHardwareCapabilityProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly ConcurrentDictionary<string, Lazy<Task<ExportHardwareCapabilities>>> Cache =
        new(StringComparer.Ordinal);

    private readonly string _executablePath;
    private readonly string _cacheKey;

    public FfmpegHardwareCapabilityProbe(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        var info = new FileInfo(_executablePath);
        if (!info.Exists)
        {
            throw new ExportException(
                ExportFailure.ToolUnavailable,
                "The configured FFmpeg executable does not exist.");
        }

        _cacheKey = string.Join(
            '|',
            _executablePath,
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }

    public Task<ExportHardwareCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var task = Cache.GetOrAdd(
            _cacheKey,
            _ => new Lazy<Task<ExportHardwareCapabilities>>(
                ProbeAllAsync,
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return task.WaitAsync(cancellationToken);
    }

    private async Task<ExportHardwareCapabilities> ProbeAllAsync()
    {
        var capabilities = new List<ExportVideoEncoderCapability>
        {
            await ProbeEncoderAsync(SoftwareProbe).ConfigureAwait(false),
        };
        foreach (var probe in CreateH264Probes())
        {
            capabilities.Add(await ProbeEncoderAsync(probe).ConfigureAwait(false));
        }

        return new ExportHardwareCapabilities(capabilities);
    }

    private async Task<ExportVideoEncoderCapability> ProbeEncoderAsync(EncoderProbe probe)
    {
        var stopwatch = Stopwatch.StartNew();
        using var process = new DiagnosticProcess
        {
            StartInfo = CreateStartInfo(probe.Arguments),
        };
        try
        {
            if (!process.Start())
            {
                return Unavailable(probe, "FFmpeg could not be started.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                TryKill(process);
                await WaitForExitAsync(process).ConfigureAwait(false);
                await standardOutput.ConfigureAwait(false);
                var timedOutError = await standardError.ConfigureAwait(false);
                return Unavailable(
                    probe,
                    string.IsNullOrWhiteSpace(timedOutError)
                        ? "Capability test timed out."
                        : $"Capability test timed out: {CreateDiagnostic(timedOutError)}");
            }

            await standardOutput.ConfigureAwait(false);
            var diagnostics = await standardError.ConfigureAwait(false);
            stopwatch.Stop();
            return process.ExitCode == 0
                ? new ExportVideoEncoderCapability(
                    probe.Encoder,
                    probe.DisplayName,
                    true,
                    $"Available · {probe.FfmpegEncoderName} · {stopwatch.Elapsed.TotalSeconds:0.00}s self-test",
                    stopwatch.Elapsed)
                : Unavailable(probe, CreateDiagnostic(diagnostics));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            TryKill(process);
            return Unavailable(probe, exception.Message);
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static IReadOnlyList<EncoderProbe> CreateH264Probes() =>
    [
        new(
            ExportVideoEncoder.NvidiaNvenc,
            "NVIDIA NVENC",
            "h264_nvenc",
            CommonProbeArguments("h264_nvenc")),
        new(
            ExportVideoEncoder.IntelQuickSync,
            "Intel Quick Sync",
            "h264_qsv",
            CommonProbeArguments("h264_qsv")),
        new(
            ExportVideoEncoder.AmdAmf,
            "AMD AMF",
            "h264_amf",
            CommonProbeArguments("h264_amf")),
        new(
            ExportVideoEncoder.Vaapi,
            "VA-API",
            "h264_vaapi",
            [
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-init_hw_device", "vaapi=clipeditva",
                "-filter_hw_device", "clipeditva",
                "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=2",
                "-frames:v", "120", "-an",
                "-vf", "format=nv12,hwupload",
                "-c:v", "h264_vaapi",
                "-f", "null", "-",
            ]),
    ];

    private static EncoderProbe SoftwareProbe { get; } = new(
        ExportVideoEncoder.Software,
        "Software (x264)",
        "libx264",
        CommonProbeArguments("libx264"));

    private static IReadOnlyList<string> CommonProbeArguments(string encoder) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error",
        "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=2",
        "-frames:v", "120", "-an",
        "-pix_fmt", "yuv420p",
        "-c:v", encoder,
        "-f", "null", "-",
    ];

    private static ExportVideoEncoderCapability Unavailable(
        EncoderProbe probe,
        string details) => new(
        probe.Encoder,
        probe.DisplayName,
        false,
        string.IsNullOrWhiteSpace(details) ? "Unavailable." : $"Unavailable · {details}");

    private static string CreateDiagnostic(string diagnostics)
    {
        var detail = diagnostics.Trim();
        if (detail.Length > 320)
        {
            detail = detail[^320..];
        }

        return string.IsNullOrWhiteSpace(detail) ? "Capability test failed." : detail;
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
        }
    }

    private static async Task WaitForExitAsync(DiagnosticProcess process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal sealed record EncoderProbe(
        ExportVideoEncoder Encoder,
        string DisplayName,
        string FfmpegEncoderName,
        IReadOnlyList<string> Arguments);
}
