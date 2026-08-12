namespace ClipEdit.Media.Analysis;

public enum WaveformRenderFailure
{
    ToolUnavailable,
    SourceUnavailable,
    ToolFailed,
    TimedOut,
    OutputTooLarge,
    NoWaveform,
}

public sealed class WaveformRenderException : Exception
{
    public WaveformRenderException(
        WaveformRenderFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public WaveformRenderFailure Failure { get; }
}
