namespace ClipEdit.App.ViewModels;

public sealed class RecoveryCandidateViewModel
{
    public RecoveryCandidateViewModel(
        string recoveryPath,
        Guid projectId,
        DateTimeOffset lastModified,
        IReadOnlyList<string> referencedMedia,
        string? errorText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        RecoveryPath = Path.GetFullPath(recoveryPath);
        ProjectId = projectId;
        LastModified = lastModified;
        ReferencedMedia = referencedMedia ?? throw new ArgumentNullException(nameof(referencedMedia));
        ErrorText = errorText;
    }

    public string RecoveryPath { get; }

    public Guid ProjectId { get; }

    public DateTimeOffset LastModified { get; }

    public IReadOnlyList<string> ReferencedMedia { get; }

    public string? ErrorText { get; }

    public bool CanRecover => ProjectId != Guid.Empty && string.IsNullOrWhiteSpace(ErrorText);

    public string Title => CanRecover
        ? $"Autosave from {LastModified.LocalDateTime:g}"
        : $"Unreadable autosave from {LastModified.LocalDateTime:g}";

    public string ReferencedMediaText => ReferencedMedia.Count switch
    {
        0 => ErrorText ?? "No referenced media",
        <= 3 => string.Join(", ", ReferencedMedia),
        _ => $"{string.Join(", ", ReferencedMedia.Take(3))} and {ReferencedMedia.Count - 3} more",
    };

    public string AutomationName => CanRecover
        ? $"Recover autosave referencing {ReferencedMediaText}"
        : $"Discard unreadable autosave: {ErrorText}";
}
