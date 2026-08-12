namespace ClipEdit.Media.Export;

public interface IExportRenderer
{
    Task<ExportResult> RenderAsync(
        ExportPlan plan,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record ExportProgress(double Fraction, string Phase, TimeSpan EncodedDuration)
{
    public double Fraction { get; } = Math.Clamp(Fraction, 0, 1);
}

public sealed record ExportResult(string DestinationPath, long FileSizeBytes, TimeSpan Elapsed);

public enum ExportFailure
{
    ToolUnavailable,
    SourceUnavailable,
    DestinationExists,
    DestinationUnavailable,
    ToolFailed,
    EmptyOutput,
}

public sealed class ExportException : Exception
{
    public ExportException(ExportFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ExportFailure Failure { get; }
}
