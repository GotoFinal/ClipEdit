namespace ClipEdit.Media.Frames;

public enum FrameDecodeFailure
{
    ToolUnavailable,
    SourceUnavailable,
    TimedOut,
    ToolFailed,
    OutputTooLarge,
    NoFrame,
}

public sealed class FrameDecodeException : Exception
{
    public FrameDecodeException(
        FrameDecodeFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public FrameDecodeFailure Failure { get; }
}
