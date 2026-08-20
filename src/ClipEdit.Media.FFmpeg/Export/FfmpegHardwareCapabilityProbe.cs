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
        VideoCodecFamily videoCodec,
        int? hardwareDeviceIndex = null,
        CancellationToken cancellationToken = default)
    {
        if (videoCodec == VideoCodecFamily.Gif)
        {
            return Task.FromResult(new ExportHardwareCapabilities([]));
        }

        var cacheKey = $"{_cacheKey}|{videoCodec}|{hardwareDeviceIndex?.ToString() ?? "auto"}";
        var task = Cache.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<ExportHardwareCapabilities>>(
                () => ProbeAllAsync(videoCodec, hardwareDeviceIndex),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return task.WaitAsync(cancellationToken);
    }

    private async Task<ExportHardwareCapabilities> ProbeAllAsync(
        VideoCodecFamily videoCodec,
        int? hardwareDeviceIndex)
    {
        var capabilities = new List<ExportVideoEncoderCapability>();
        foreach (var probe in CreateProbes(videoCodec, hardwareDeviceIndex))
        {
            if (hardwareDeviceIndex is not null && probe.Encoder == ExportVideoEncoder.AmdAmf)
            {
                capabilities.Add(new ExportVideoEncoderCapability(
                    probe.Encoder,
                    probe.DisplayName,
                    false,
                    "Unavailable · FFmpeg AMF cannot select an exact adapter; use Auto GPU.",
                    VideoCodec: probe.VideoCodec));
                continue;
            }

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
                    stopwatch.Elapsed,
                    probe.VideoCodec)
                : Unavailable(probe, CreateDiagnostic(diagnostics));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
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

    internal static IReadOnlyList<EncoderProbe> CreateProbes(
        VideoCodecFamily videoCodec,
        int? hardwareDeviceIndex = null)
    {
        var probes = new List<EncoderProbe> { CreateSoftwareProbe(videoCodec) };
        foreach (var backend in new[]
                 {
                     ExportVideoEncoder.NvidiaNvenc,
                     ExportVideoEncoder.IntelQuickSync,
                     ExportVideoEncoder.AmdAmf,
                     ExportVideoEncoder.Vaapi,
                 })
        {
            if (TryGetEncoderName(videoCodec, backend, out var encoderName))
            {
                probes.Add(CreateHardwareProbe(videoCodec, backend, encoderName, hardwareDeviceIndex));
            }
        }

        return probes;
    }

    internal static IReadOnlyList<EncoderProbe> CreateH264Probes(int? hardwareDeviceIndex = null) =>
        CreateProbes(VideoCodecFamily.H264, hardwareDeviceIndex)
            .Where(probe => probe.Encoder != ExportVideoEncoder.Software)
            .ToArray();

    private static EncoderProbe CreateSoftwareProbe(VideoCodecFamily videoCodec)
    {
        var encoderName = videoCodec switch
        {
            VideoCodecFamily.H264 => "libx264",
            VideoCodecFamily.Hevc => "libx265",
            VideoCodecFamily.Vp8 => "libvpx",
            VideoCodecFamily.Vp9 => "libvpx-vp9",
            VideoCodecFamily.Av1 => "libaom-av1",
            _ => throw new ArgumentOutOfRangeException(nameof(videoCodec), videoCodec, "Unsupported probe codec."),
        };
        return new EncoderProbe(
            ExportVideoEncoder.Software,
            $"Software ({encoderName})",
            encoderName,
            videoCodec,
            CommonProbeArguments(encoderName, videoCodec));
    }

    private static EncoderProbe CreateHardwareProbe(
        VideoCodecFamily videoCodec,
        ExportVideoEncoder backend,
        string encoderName,
        int? hardwareDeviceIndex)
    {
        var displayName = backend switch
        {
            ExportVideoEncoder.NvidiaNvenc => "NVIDIA NVENC",
            ExportVideoEncoder.IntelQuickSync => "Intel Quick Sync",
            ExportVideoEncoder.AmdAmf => "AMD AMF",
            ExportVideoEncoder.Vaapi => "VA-API",
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unsupported hardware backend."),
        };
        return new EncoderProbe(
            backend,
            displayName,
            encoderName,
            videoCodec,
            HardwareProbeArguments(backend, encoderName, videoCodec, hardwareDeviceIndex));
    }

    private static bool TryGetEncoderName(
        VideoCodecFamily codec,
        ExportVideoEncoder backend,
        out string encoderName)
    {
        encoderName = (codec, backend) switch
        {
            (VideoCodecFamily.H264, ExportVideoEncoder.NvidiaNvenc) => "h264_nvenc",
            (VideoCodecFamily.H264, ExportVideoEncoder.IntelQuickSync) => "h264_qsv",
            (VideoCodecFamily.H264, ExportVideoEncoder.AmdAmf) => "h264_amf",
            (VideoCodecFamily.H264, ExportVideoEncoder.Vaapi) => "h264_vaapi",
            (VideoCodecFamily.Hevc, ExportVideoEncoder.NvidiaNvenc) => "hevc_nvenc",
            (VideoCodecFamily.Hevc, ExportVideoEncoder.IntelQuickSync) => "hevc_qsv",
            (VideoCodecFamily.Hevc, ExportVideoEncoder.AmdAmf) => "hevc_amf",
            (VideoCodecFamily.Hevc, ExportVideoEncoder.Vaapi) => "hevc_vaapi",
            (VideoCodecFamily.Vp8, ExportVideoEncoder.Vaapi) => "vp8_vaapi",
            (VideoCodecFamily.Vp9, ExportVideoEncoder.IntelQuickSync) => "vp9_qsv",
            (VideoCodecFamily.Vp9, ExportVideoEncoder.Vaapi) => "vp9_vaapi",
            (VideoCodecFamily.Av1, ExportVideoEncoder.NvidiaNvenc) => "av1_nvenc",
            (VideoCodecFamily.Av1, ExportVideoEncoder.IntelQuickSync) => "av1_qsv",
            (VideoCodecFamily.Av1, ExportVideoEncoder.AmdAmf) => "av1_amf",
            (VideoCodecFamily.Av1, ExportVideoEncoder.Vaapi) => "av1_vaapi",
            _ => string.Empty,
        };
        return encoderName.Length > 0;
    }

    private static IReadOnlyList<string> CommonProbeArguments(
        string encoder,
        VideoCodecFamily videoCodec)
    {
        var (source, frames) = ProbeWorkload(videoCodec);
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi", "-i", source,
            "-frames:v", frames, "-an",
            "-pix_fmt", "yuv420p",
            "-c:v", encoder,
        };
        AddSoftwareProbeSpeed(arguments, encoder);
        arguments.AddRange(["-f", "null", "-"]);
        return arguments;
    }

    private static IReadOnlyList<string> HardwareProbeArguments(
        ExportVideoEncoder backend,
        string encoder,
        VideoCodecFamily videoCodec,
        int? hardwareDeviceIndex)
    {
        if (backend == ExportVideoEncoder.Vaapi)
        {
            return VaapiProbeArguments(encoder, videoCodec, hardwareDeviceIndex);
        }

        var arguments = CommonProbeArguments(encoder, videoCodec).ToList();
        if (hardwareDeviceIndex is not { } deviceIndex)
        {
            return arguments;
        }

        switch (backend)
        {
            case ExportVideoEncoder.NvidiaNvenc:
                InsertBeforeOutput(arguments, "-gpu", deviceIndex.ToString());
                break;
            case ExportVideoEncoder.IntelQuickSync:
                arguments.InsertRange(4, ["-qsv_device", deviceIndex.ToString()]);
                break;
            case ExportVideoEncoder.AmdAmf:
                // FFmpeg's AMF encoders do not expose a stable adapter-index option.
                break;
        }

        return arguments;
    }

    private static IReadOnlyList<string> VaapiProbeArguments(
        string encoder,
        VideoCodecFamily videoCodec,
        int? hardwareDeviceIndex)
    {
        var (source, frames) = ProbeWorkload(videoCodec);
        return
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-init_hw_device", hardwareDeviceIndex is { } deviceIndex
                ? $"vaapi=clipeditva:{deviceIndex}"
                : "vaapi=clipeditva",
            "-filter_hw_device", "clipeditva",
            "-f", "lavfi", "-i", source,
            "-frames:v", frames, "-an",
            "-vf", "format=nv12,hwupload",
            "-c:v", encoder,
            "-f", "null", "-",
        ];
    }

    private static void InsertBeforeOutput(List<string> arguments, string option, string value)
    {
        var outputIndex = arguments.FindLastIndex(argument => argument == "-");
        if (outputIndex < 0)
        {
            outputIndex = arguments.Count;
        }

        arguments.Insert(outputIndex, option);
        arguments.Insert(outputIndex + 1, value);
    }

    private static (string Source, string Frames) ProbeWorkload(VideoCodecFamily videoCodec) =>
        videoCodec == VideoCodecFamily.H264
            ? ("testsrc2=s=1920x1080:r=60:d=2", "120")
            : ("testsrc2=s=640x360:r=30:d=1", "30");

    private static void AddSoftwareProbeSpeed(List<string> arguments, string encoder)
    {
        switch (encoder)
        {
            case "libx264":
            case "libx265":
                arguments.Add("-preset");
                arguments.Add("medium");
                break;
            case "libvpx":
            case "libvpx-vp9":
                arguments.AddRange(["-deadline", "good", "-cpu-used", "4"]);
                break;
            case "libaom-av1":
                arguments.AddRange(["-cpu-used", "6"]);
                break;
        }
    }

    private static ExportVideoEncoderCapability Unavailable(
        EncoderProbe probe,
        string details) => new(
        probe.Encoder,
        probe.DisplayName,
        false,
        string.IsNullOrWhiteSpace(details) ? "Unavailable." : $"Unavailable · {details}",
        VideoCodec: probe.VideoCodec);

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
        VideoCodecFamily VideoCodec,
        IReadOnlyList<string> Arguments);
}
