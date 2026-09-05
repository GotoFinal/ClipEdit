using System.Diagnostics;
using System.Text.Json;

namespace ClipEdit.Media.FFmpeg.Process;

public static class MediaProcessDiagnostics
{
    public static Action<string>? Sink { get; set; }

    public static void WriteStandardError(System.Diagnostics.Process process, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Write($"Process pid={process.Id} stderr: {text}");
        }
    }

    public static void Write(string message)
    {
        try
        {
            Sink?.Invoke(message);
        }
        catch (Exception)
        {
            // Logging must never interrupt editing or media processing.
        }
    }

    public static bool Start(System.Diagnostics.Process process)
    {
        if (Sink is null)
        {
            return process.Start();
        }
        var info = process.StartInfo;
        var command = string.Join(" ", new[] { info.FileName }.Concat(info.ArgumentList)
            .Select(argument => JsonSerializer.Serialize(argument)));
        var request = Guid.NewGuid().ToString("N")[..8];
        Write($"Process {request} command: {command}");
        var clock = Stopwatch.StartNew();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try
            {
                Write($"Process {request} exit={process.ExitCode} elapsed={clock.Elapsed.TotalSeconds:F3}s");
            }
            catch (InvalidOperationException)
            {
                // Disposal can race the exit notification.
            }
        };
        try
        {
            var started = process.Start();
            if (started)
            {
                Write($"Process {request} started pid={process.Id}");
            }
            if (!started)
            {
                Write($"Process {request} failed to start.");
            }
            return started;
        }
        catch (Exception exception)
        {
            Write($"Process {request} failed to start: {exception}");
            throw;
        }
    }
}
