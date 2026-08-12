namespace ClipEdit.Media.Probe;

public enum MediaProbeFailure
{
    ToolUnavailable,
    SourceUnavailable,
    TimedOut,
    ToolFailed,
    OutputTooLarge,
    InvalidOutput,
}

public sealed class MediaProbeException : Exception
{
    public MediaProbeException(
        MediaProbeFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public MediaProbeFailure Failure { get; }
}
