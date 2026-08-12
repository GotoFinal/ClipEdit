namespace ClipEdit.Media.Mpv;

public sealed class MpvPreviewException : Exception
{
    public MpvPreviewException(string message)
        : base(message)
    {
    }

    public MpvPreviewException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
