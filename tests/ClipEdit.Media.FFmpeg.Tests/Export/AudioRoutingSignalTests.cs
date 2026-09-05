using System.Diagnostics;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;
using ClipEdit.Media.FFmpeg.Process;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class AudioRoutingSignalTests
{
    [Fact]
    public async Task Media_process_log_records_the_command_and_exit()
    {
        var ffmpeg = FfmpegToolLocator.FindFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousSink = MediaProcessDiagnostics.Sink;
        MediaProcessDiagnostics.Sink = message =>
        {
            messages.Enqueue(message);
            if (message.Contains("exit=0")) exited.TrySetResult();
        };
        try
        {
            Assert.NotEmpty(await Run(ffmpeg, ["-version"]));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(messages, message => message.Contains("command:") && message.Contains("-version"));
            Assert.Contains(messages, message => message.Contains("started pid="));
            Assert.Contains(messages, message => message.Contains("exit=0"));
        }
        finally
        {
            MediaProcessDiagnostics.Sink = previousSink;
        }
    }

    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Long_source_trim_preserves_the_second_audio_signal()
    {
        var source = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MULTI_AUDIO");
        var startText = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_AUDIO_TRIM_START_SECONDS");
        var ffmpeg = FfmpegToolLocator.FindFfmpeg();
        if (source is null || startText is null || ffmpeg is null)
        {
            return;
        }
        var start = new MediaTime(int.Parse(startText, System.Globalization.CultureInfo.InvariantCulture), 1);
        var range = new MediaRange(start, start + new MediaTime(10, 1));
        var size = new PixelSize(160, 90);
        var signature = new VideoStreamCopySignature("h264", "avc1", "test", size,
            new MediaTime(1, 1000), new FrameRate(25, 1), "yuv420p", null, null,
            "1:1", null, null, null, null, "progressive");
        var segment = new ExportVideoSegmentPlan(source, 0, range, size, CropRegion.FullFrame(size),
            ClipCanvasTransform.Identity,
            [new ExportAudioTrackPlan(1, 0, 0), new ExportAudioTrackPlan(2, 0, 1)],
            MediaTime.Zero,
            streamCopyInfo: new SegmentStreamCopyInfo(signature, null, true, true, range.Start, range.End));
        var plan = new ExportPlan([segment], size, Path.GetFullPath("unused.mp4"),
            new ExportPreset("audio-test", "Audio test", ".mp4", ExportContainer.Mp4,
                VideoCodecFamily.H264, AudioCodecFamily.Aac, requiresEvenDimensions: true),
            sequenceDuration: range.Duration, strategy: ExportStrategy.VideoStreamCopy);
        var arguments = new List<string> { "-f", "lavfi", "-i", "color=s=16x16:d=0.1" };
        FfmpegExportArguments.AddBoundedAudioInput(arguments, segment);
        arguments.AddRange(["-filter_complex",
            FfmpegExportArguments.CreateVideoStreamCopyAudioFilterGraph(plan, usesSeparateAudioInput: true)]);
        // Keep both graph outputs active, measuring the formerly silent second lane.
        arguments.AddRange(["-map", "[aout0]", "-f", "null", "-",
            "-map", "[aout1]", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
        var rendered = await Run(ffmpeg, arguments);
        var original = await Run(ffmpeg,
            ["-ss", startText, "-t", "10", "-i", source, "-map", "0:a:1",
             "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
        static double Rms(byte[] bytes) => Math.Sqrt(Enumerable.Range(0, bytes.Length / 4)
            .Average(index => Math.Pow(BitConverter.ToSingle(bytes, index * 4), 2)));
        Assert.True(Rms(original) > 0.00001, "Choose a source interval containing audio in its second lane.");
        Assert.InRange(Rms(rendered) / Rms(original), 0.95, 1.05);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Separate_outputs_retain_both_distinct_audio_signals(bool copyVideo, bool trim)
    {
        var ffmpeg = FfmpegToolLocator.FindFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("clipedit-audio-signals-");
        var source = Path.Combine(directory.FullName, "source.mkv");
        var output = Path.Combine(directory.FullName, "output.mp4");
        try
        {
            await Run(ffmpeg,
            [
                "-f", "lavfi", "-i", "color=s=160x90:r=25:d=120",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=120",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=120",
                "-map", "0:v", "-map", "1:a", "-map", "2:a",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "25", "-c:a", "aac", source,
            ]);
            var size = new PixelSize(160, 90);
            var duration = new MediaTime(120, 1);
            var range = new MediaRange(new MediaTime(trim ? 100 : 0, 1), new MediaTime(trim ? 102 : 120, 1));
            var signature = new VideoStreamCopySignature("h264", "avc1", "test", size,
                new MediaTime(1, 1000), new FrameRate(25, 1), "yuv420p", "Constrained Baseline", 11,
                "1:1", null, null, null, null, "progressive");
            var plan = new ExportPlan(
                [new ExportVideoSegmentPlan(
                    source, 0, range, size, CropRegion.FullFrame(size), ClipCanvasTransform.Identity,
                    [new ExportAudioTrackPlan(1, 0, new SourceEdit(duration), 0),
                     new ExportAudioTrackPlan(2, 0, new SourceEdit(duration), 1)],
                    MediaTime.Zero, isCompleteSource: !trim,
                    streamCopyInfo: new SegmentStreamCopyInfo(signature, null, true, true, range.Start, range.End))],
                size, output,
                new ExportPreset("audio-test", "Audio test", ".mp4", ExportContainer.Mp4,
                    VideoCodecFamily.H264, AudioCodecFamily.Aac, requiresEvenDimensions: true),
                sequenceDuration: range.Duration,
                strategy: copyVideo ? ExportStrategy.VideoStreamCopy : ExportStrategy.ExactTranscode);
            // Exercise the chosen path directly so a validation fallback cannot conceal broken audio.
            await Run(ffmpeg, FfmpegExportArguments.Create(plan, output));

            for (var track = 0; track < 2; track++)
            {
                var bytes = await Run(ffmpeg,
                ["-i", output, "-map", $"0:a:{track}", "-ss", "0.25", "-t", "1",
                 "-ac", "1", "-ar", "48000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"]);
                Assert.True(bytes.Length >= 48_000 * sizeof(float), $"Track {track + 1} is empty.");
                var samples = Enumerable.Range(0, bytes.Length / sizeof(float))
                    .Select(index => (double)BitConverter.ToSingle(bytes, index * sizeof(float))).ToArray();
                var rms = Math.Sqrt(samples.Average(value => value * value));
                Assert.True(rms > 0.03, $"Track {track + 1} is silent: RMS={rms}.");
                var expectedFrequency = track == 0 ? 440 : 880;
                var crossings = samples.Zip(samples.Skip(1)).Count(pair => pair.First <= 0 && pair.Second > 0);
                Assert.InRange(crossings * 48_000d / samples.Length, expectedFrequency - 5, expectedFrequency + 5);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<byte[]> Run(string executable, IEnumerable<string> arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-nostdin");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        MediaProcessDiagnostics.Start(process);
        using var output = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token),
                process.WaitForExitAsync(timeout.Token));
            Assert.True(process.ExitCode == 0, await errors);
            return output.ToArray();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
