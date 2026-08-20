using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using DiagnosticProcess = System.Diagnostics.Process;

namespace ClipEdit.Media.FFmpeg.Export;

public sealed class FfmpegExportRenderer : IExportRenderer, IExportHardwareCapabilityProbe
{
    private const int MaximumDiagnosticCharacters = 256 * 1024;
    private readonly string _executablePath;
    private readonly string? _ffprobeExecutablePath;
    private readonly FfmpegHardwareCapabilityProbe _hardwareCapabilityProbe;

    public FfmpegExportRenderer(string executablePath, string? ffprobeExecutablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(_executablePath))
        {
            throw new ExportException(
                ExportFailure.ToolUnavailable,
                "The configured FFmpeg executable does not exist.");
        }
        _hardwareCapabilityProbe = new FfmpegHardwareCapabilityProbe(_executablePath);
        if (!string.IsNullOrWhiteSpace(ffprobeExecutablePath))
        {
            _ffprobeExecutablePath = Path.GetFullPath(ffprobeExecutablePath);
            if (!File.Exists(_ffprobeExecutablePath))
            {
                throw new ExportException(
                    ExportFailure.ToolUnavailable,
                    "The configured ffprobe executable does not exist.");
            }
        }
    }

    public Task<ExportHardwareCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default) =>
        _hardwareCapabilityProbe.ProbeAsync(cancellationToken);

    public async Task<ExportResult> RenderAsync(
        ExportPlan plan,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePaths(plan);

        if (plan.Strategy == ExportStrategy.BoundaryGop)
        {
            try
            {
                return await RenderBoundaryGopAsync(plan, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ExportException)
            {
                progress?.Report(new ExportProgress(
                    0,
                    "Boundary-GOP rejected · encoding exactly",
                    TimeSpan.Zero));
                return await RenderSingleProcessWithHardwareFallbackAsync(
                        plan,
                        progress,
                        cancellationToken,
                        forceExactTranscode: true)
                    .ConfigureAwait(false);
            }
        }

        return await RenderSingleProcessWithHardwareFallbackAsync(plan, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ExportResult> RenderSingleProcessWithHardwareFallbackAsync(
        ExportPlan plan,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken,
        bool forceExactTranscode = false)
    {
        var usesExactTranscode = forceExactTranscode || plan.Strategy == ExportStrategy.ExactTranscode;
        var requestedVideoEncoder = FfmpegExportArguments.ResolveVideoEncoder(plan);
        try
        {
            return await RenderSingleProcessAsync(
                    plan,
                    progress,
                    cancellationToken,
                    forceExactTranscode)
                .ConfigureAwait(false);
        }
        catch (ExportException exception) when (
            usesExactTranscode &&
            (plan.EncodingSettings.HardwareAcceleration != ExportHardwareAcceleration.Software ||
             requestedVideoEncoder != ExportVideoEncoder.Software) &&
            IsHardwareAccelerationFailure(exception))
        {
            var failedComponent = requestedVideoEncoder != ExportVideoEncoder.Software
                ? "Hardware encoder"
                : "Hardware decode";
            progress?.Report(new ExportProgress(
                0,
                $"{failedComponent} unavailable · retrying software",
                TimeSpan.Zero));
            return await RenderSingleProcessAsync(
                    plan,
                    progress,
                    cancellationToken,
                    forceExactTranscode,
                    ExportHardwareAcceleration.Software,
                    ExportVideoEncoder.Software)
                .ConfigureAwait(false);
        }
    }

    private async Task<ExportResult> RenderSingleProcessAsync(
        ExportPlan plan,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken,
        bool forceExactTranscode = false,
        ExportHardwareAcceleration? hardwareAccelerationOverride = null,
        ExportVideoEncoder? videoEncoderOverride = null)
    {
        var temporaryPath = CreateTemporaryPath(plan.DestinationPath);
        var concatManifestPath = !forceExactTranscode && plan.Strategy == ExportStrategy.ConcatStreamCopy
            ? temporaryPath + ".ffconcat"
            : null;
        if (concatManifestPath is not null)
        {
            await WriteConcatManifestAsync(plan, concatManifestPath, cancellationToken)
                .ConfigureAwait(false);
        }
        using var process = new DiagnosticProcess
        {
            StartInfo = CreateStartInfo(
                forceExactTranscode || hardwareAccelerationOverride is not null || videoEncoderOverride is not null
                    ? FfmpegExportArguments.CreateExactTranscode(
                        plan,
                        temporaryPath,
                        hardwareAccelerationOverride,
                        videoEncoderOverride)
                    : FfmpegExportArguments.Create(plan, temporaryPath, concatManifestPath)),
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            StartProcess(process);
            var activePhase = !forceExactTranscode &&
                              (plan.Strategy is ExportStrategy.StreamCopy or
                                  ExportStrategy.EditListStreamCopy or
                                  ExportStrategy.ConcatStreamCopy)
                ? "Copying"
                : "Encoding";
            progress?.Report(new ExportProgress(0, activePhase, TimeSpan.Zero));

            var progressTask = ReadProgressAsync(
                process.StandardOutput,
                plan,
                progress,
                activePhase,
                stopwatch);
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
            var actualStrategy = forceExactTranscode ? ExportStrategy.ExactTranscode : plan.Strategy;
            var actualVideoEncoder = actualStrategy == ExportStrategy.ExactTranscode
                ? (ExportVideoEncoder?)FfmpegExportArguments.ResolveVideoEncoder(plan, videoEncoderOverride)
                : null;
            return new ExportResult(
                plan.DestinationPath,
                fileInfo.Length,
                stopwatch.Elapsed,
                actualStrategy,
                actualVideoEncoder);
        }
        catch
        {
            TryKill(process);
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            if (concatManifestPath is not null)
            {
                TryDelete(concatManifestPath);
            }
        }
    }

    private static void ValidatePaths(ExportPlan plan)
    {
        if (plan.IsSequence
                ? plan.VideoSegments.Any(segment => !File.Exists(segment.SourcePath))
                : !File.Exists(plan.SourcePath))
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
        if ((plan.IsSequence
                ? plan.VideoSegments.Any(segment =>
                    string.Equals(segment.SourcePath, plan.DestinationPath, pathComparison))
                : string.Equals(plan.SourcePath, plan.DestinationPath, pathComparison)))
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

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments) =>
        CreateStartInfo(_executablePath, arguments);

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
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
        IProgress<ExportProgress>? progress,
        string activePhase,
        Stopwatch stopwatch)
    {
        var parser = new FfmpegProgressParser();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (!parser.Parse(line) || !parser.IsReportBoundary)
            {
                continue;
            }

            var expectedSeconds = plan.ExpectedDuration.TotalSeconds;
            var fraction = expectedSeconds <= 0
                ? 0
                : Math.Min(0.98, parser.EncodedDuration.TotalSeconds / expectedSeconds);
            progress?.Report(new ExportProgress(
                fraction,
                activePhase,
                parser.EncodedDuration,
                parser.FramesPerSecond,
                EstimateRemaining(
                    plan.ExpectedDurationToTimeSpan(),
                    parser.EncodedDuration,
                    parser.ProcessingSpeed,
                    stopwatch.Elapsed)));
        }
    }

    private async Task<ExportResult> RenderBoundaryGopAsync(
        ExportPlan plan,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_ffprobeExecutablePath is null)
        {
            throw new ExportException(
                ExportFailure.ToolUnavailable,
                "Boundary-GOP validation requires ffprobe.");
        }

        var segment = plan.VideoSegments.Single();
        var boundary = segment.BoundaryGopInfo ?? throw new ExportException(
            ExportFailure.ToolFailed,
            "Boundary-GOP planning data is missing.");
        var temporaryPath = CreateTemporaryPath(plan.DestinationPath);
        var workDirectory = temporaryPath + ".boundary-gop";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(workDirectory);
            var extension = plan.Preset.FileExtension;
            var pieces = new List<string>(3);
            if (boundary.HasLeadingBoundary)
            {
                progress?.Report(new ExportProgress(0.03, "Encoding first cut GOP", TimeSpan.Zero));
                var leadingPath = Path.Combine(workDirectory, "leading" + extension);
                var leadingRange = new MediaRange(
                    segment.SourceRange.Start,
                    boundary.CopiedStartPresentationTimestamp);
                await RenderBoundaryPieceAsync(plan, segment, leadingRange, leadingPath, cancellationToken)
                    .ConfigureAwait(false);
                pieces.Add(leadingPath);
            }

            progress?.Report(new ExportProgress(0.28, "Copying interior GOPs", TimeSpan.Zero));
            var interiorPath = Path.Combine(workDirectory, "interior" + extension);
            await RunUtilityProcessAsync(
                    _executablePath,
                    FfmpegBoundaryGopArguments.CreateInteriorCopy(plan, interiorPath),
                    cancellationToken)
                .ConfigureAwait(false);
            pieces.Add(interiorPath);

            if (boundary.HasTrailingBoundary)
            {
                progress?.Report(new ExportProgress(0.5, "Encoding last cut GOP", TimeSpan.Zero));
                var trailingPath = Path.Combine(workDirectory, "trailing" + extension);
                var trailingRange = new MediaRange(
                    boundary.CopiedEndPresentationTimestamp,
                    segment.SourceRange.End);
                await RenderBoundaryPieceAsync(plan, segment, trailingRange, trailingPath, cancellationToken)
                    .ConfigureAwait(false);
                pieces.Add(trailingPath);
            }

            var manifestPath = Path.Combine(workDirectory, "pieces.ffconcat");
            await File.WriteAllTextAsync(
                    manifestPath,
                    FfconcatManifest.CreatePaths(pieces),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new ExportProgress(0.72, "Joining GOPs and audio", TimeSpan.Zero));
            await RunUtilityProcessAsync(
                    _executablePath,
                    FfmpegBoundaryGopArguments.CreateFinalMux(plan, manifestPath, temporaryPath),
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ExportProgress(0.9, "Validating Boundary-GOP candidate", TimeSpan.Zero));
            await ValidateBoundaryGopOutputAsync(plan, temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            var fileInfo = new FileInfo(temporaryPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                throw new ExportException(
                    ExportFailure.EmptyOutput,
                    "Boundary-GOP rendering produced an empty candidate.");
            }

            progress?.Report(new ExportProgress(0.99, "Finalizing", plan.ExpectedDurationToTimeSpan()));
            FinalizeOutput(temporaryPath, plan);
            stopwatch.Stop();
            progress?.Report(new ExportProgress(1, "Complete · Boundary-GOP validated", plan.ExpectedDurationToTimeSpan()));
            return new ExportResult(
                plan.DestinationPath,
                fileInfo.Length,
                stopwatch.Elapsed,
                ExportStrategy.BoundaryGop);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (ExportException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            TryDelete(temporaryPath);
            throw new ExportException(
                ExportFailure.ToolFailed,
                $"Boundary-GOP candidate was rejected: {exception.Message}",
                exception);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private async Task RenderBoundaryPieceAsync(
        ExportPlan plan,
        ExportVideoSegmentPlan sourceSegment,
        MediaRange range,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var segment = new ExportVideoSegmentPlan(
            sourceSegment.SourcePath,
            sourceSegment.VideoStreamIndex,
            range,
            sourceSegment.CanvasSize,
            sourceSegment.CanvasCrop,
            sourceSegment.CanvasTransform,
            [],
            MediaTime.Zero,
            videoColorInfo: sourceSegment.VideoColorInfo,
            sourceSize: sourceSegment.SourceSize);
        var piecePlan = new ExportPlan(
            [segment],
            plan.OutputSize,
            outputPath,
            plan.Preset,
            sequenceDuration: range.Duration,
            encodingSettings: plan.EncodingSettings);
        await RunUtilityProcessAsync(
                _executablePath,
                FfmpegExportArguments.CreateExactTranscode(
                    piecePlan,
                    outputPath,
                    ExportHardwareAcceleration.Software,
                    ExportVideoEncoder.Software),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ValidateBoundaryGopOutputAsync(
        ExportPlan plan,
        string candidatePath,
        CancellationToken cancellationToken)
    {
        var segment = plan.VideoSegments.Single();
        var boundary = segment.BoundaryGopInfo!;
        var json = await RunUtilityProcessAsync(
                _ffprobeExecutablePath!,
                [
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-count_packets",
                    "-show_entries", "stream=codec_name,width,height,avg_frame_rate,nb_read_packets,duration:format=duration",
                    "-of", "json",
                    candidatePath,
                ],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) ||
            streams.GetArrayLength() != 1)
        {
            throw new InvalidDataException("The candidate does not contain exactly one video stream.");
        }

        var stream = streams[0];
        var codec = GetRequiredString(stream, "codec_name");
        var width = GetRequiredInt32(stream, "width");
        var height = GetRequiredInt32(stream, "height");
        var packetCount = GetRequiredInt64(stream, "nb_read_packets");
        var expectedCodec = boundary.Video.CodecName;
        var expectedPacketCount = CalculateExpectedFrameCount(plan.ExpectedDuration, boundary.Video.AverageFrameRate);
        if (!string.Equals(codec, expectedCodec, StringComparison.OrdinalIgnoreCase) ||
            width != plan.OutputSize.Width ||
            height != plan.OutputSize.Height ||
            packetCount != expectedPacketCount)
        {
            throw new InvalidDataException(
                $"Expected {expectedCodec} {plan.OutputSize.Width}x{plan.OutputSize.Height} with " +
                $"{expectedPacketCount} video packets, but received {codec} {width}x{height} with {packetCount}.");
        }

        var duration = TryGetSeconds(stream, "duration") ??
                       (document.RootElement.TryGetProperty("format", out var format)
                           ? TryGetSeconds(format, "duration")
                           : null);
        var frameDuration = 1d / boundary.Video.AverageFrameRate.FramesPerSecond;
        if (duration is null ||
            Math.Abs(duration.Value - plan.ExpectedDuration.TotalSeconds) > frameDuration + 0.005)
        {
            throw new InvalidDataException(
                $"Expected about {plan.ExpectedDuration.TotalSeconds:0.###} seconds, but the candidate reports " +
                $"{duration?.ToString("0.###", CultureInfo.InvariantCulture) ?? "no duration"}.");
        }

        var splicePositions = new List<MediaTime>(2);
        var firstSplice = boundary.CopiedStartPresentationTimestamp - segment.SourceRange.Start;
        if (boundary.HasLeadingBoundary)
        {
            splicePositions.Add(firstSplice);
        }
        if (boundary.HasTrailingBoundary)
        {
            splicePositions.Add(
                firstSplice + boundary.CopiedSourceRange.Duration);
        }
        foreach (var splice in splicePositions.Distinct())
        {
            var windowStart = Math.Max(0, splice.TotalSeconds - 0.5);
            var windowDuration = Math.Min(
                1,
                plan.ExpectedDuration.TotalSeconds - windowStart);
            await RunUtilityProcessAsync(
                    _executablePath,
                    [
                        "-hide_banner", "-nostdin", "-v", "error", "-xerror",
                        "-ss", windowStart.ToString("0.#########", CultureInfo.InvariantCulture),
                        "-t", windowDuration.ToString("0.#########", CultureInfo.InvariantCulture),
                        "-i", candidatePath,
                        "-map", "0:v:0", "-an", "-f", "null", "-",
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<string> RunUtilityProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new DiagnosticProcess
        {
            StartInfo = CreateStartInfo(executablePath, arguments),
        };
        StartProcess(process);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var diagnosticTask = ReadDiagnosticTailAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitWithoutCancellationAsync(process).ConfigureAwait(false);
            await IgnoreFailureAsync(outputTask).ConfigureAwait(false);
            await IgnoreFailureAsync(diagnosticTask).ConfigureAwait(false);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var diagnostics = await diagnosticTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new ExportException(
                ExportFailure.ToolFailed,
                BuildFailureMessage(process.ExitCode, diagnostics));
        }

        return output;
    }

    private static long CalculateExpectedFrameCount(MediaTime duration, FrameRate frameRate)
    {
        var numerator = (Int128)duration.Numerator * frameRate.Numerator;
        var denominator = (Int128)duration.Denominator * frameRate.Denominator;
        if (numerator % denominator != 0)
        {
            throw new InvalidDataException("The Boundary-GOP duration is not aligned to complete CFR frames.");
        }

        return checked((long)(numerator / denominator));
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"ffprobe did not report {propertyName}.");
        }

        return property.GetString()!;
    }

    private static int GetRequiredInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"ffprobe did not report {propertyName}.");
        }

        return value;
    }

    private static long GetRequiredInt64(JsonElement element, string propertyName)
    {
        var text = GetRequiredString(element, propertyName);
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"ffprobe reported an invalid {propertyName}.");
        }

        return value;
    }

    private static double? TryGetSeconds(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !double.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) ||
            !double.IsFinite(value))
        {
            return null;
        }

        return value;
    }

    private static async Task WriteConcatManifestAsync(
        ExportPlan plan,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(
                    manifestPath,
                    FfconcatManifest.Create(plan),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDelete(manifestPath);
            throw;
        }
    }

    internal static TimeSpan? EstimateRemaining(
        TimeSpan expectedDuration,
        TimeSpan encodedDuration,
        double? reportedProcessingSpeed,
        TimeSpan elapsed)
    {
        var remainingMediaSeconds = expectedDuration.TotalSeconds - encodedDuration.TotalSeconds;
        if (!double.IsFinite(remainingMediaSeconds))
        {
            return null;
        }

        if (remainingMediaSeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var effectiveSpeed = reportedProcessingSpeed is > 0 &&
                             double.IsFinite(reportedProcessingSpeed.Value)
            ? reportedProcessingSpeed.Value
            : elapsed >= TimeSpan.FromSeconds(1) && encodedDuration > TimeSpan.Zero
                ? encodedDuration.TotalSeconds / elapsed.TotalSeconds
                : double.NaN;
        var remainingWallSeconds = remainingMediaSeconds / effectiveSpeed;
        return double.IsFinite(remainingWallSeconds) &&
               remainingWallSeconds >= 0 &&
               remainingWallSeconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(remainingWallSeconds)
            : null;
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

    internal static bool IsHardwareAccelerationFailure(ExportException exception)
    {
        if (exception.Failure != ExportFailure.ToolFailed)
        {
            return false;
        }

        string[] indicators =
        [
            "device creation failed",
            "failed to create a vulkan device",
            "no device available",
            "no capable devices found",
            "failed setup for format",
            "failed to initialise",
            "failed to initialize",
            "hardware acceleration methods",
            "impossible to convert between the formats",
            "a hardware device reference is required",
            "error while opening encoder",
            "failed to create encoder",
            "cannot load nvcuda",
            "cannot load libcuda",
            "no nvenc capable devices found",
            "failed to initialise vaapi connection",
            "failed to initialize vaapi connection",
            "device failed",
        ];
        return indicators.Any(indicator =>
            exception.Message.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Boundary-GOP scratch data is private and may be removed on a later cleanup pass.
        }
        catch (UnauthorizedAccessException)
        {
            // Boundary-GOP scratch data is private and may be removed on a later cleanup pass.
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
