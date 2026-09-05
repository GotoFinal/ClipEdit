using System.Text;

namespace ClipEdit.App.Diagnostics;

public sealed class SessionLog : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipEdit", "ClipEdit.log");

    public SessionLog(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
            { AutoFlush = true };
            Write($"ClipEdit {typeof(SessionLog).Assembly.GetName().Version}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; process {Environment.ProcessId}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only profile must not prevent the app from opening.
        }
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                _writer?.WriteLine($"{DateTimeOffset.Now:O} {message}");
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A logging failure must not affect playback or export.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); }
            catch (IOException) { }
            _writer = null;
        }
    }
}
