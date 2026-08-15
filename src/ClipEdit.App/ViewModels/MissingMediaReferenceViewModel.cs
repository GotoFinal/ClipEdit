using ClipEdit.Application.Projects;

namespace ClipEdit.App.ViewModels;

public sealed class MissingMediaReferenceViewModel : ViewModelBase
{
    private string? _replacementPath;

    public MissingMediaReferenceViewModel(
        ProjectMediaDocument savedMedia,
        string reason,
        string? suggestedPath = null)
    {
        SavedMedia = savedMedia ?? throw new ArgumentNullException(nameof(savedMedia));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
        SuggestedPath = string.IsNullOrWhiteSpace(suggestedPath)
            ? null
            : Path.GetFullPath(suggestedPath);
    }

    internal ProjectMediaDocument SavedMedia { get; }

    public Guid MediaId => SavedMedia.MediaId;

    public string OriginalPath => SavedMedia.SourcePath;

    public string DisplayName
    {
        get
        {
            var name = Path.GetFileName(OriginalPath);
            return string.IsNullOrWhiteSpace(name) ? OriginalPath : name;
        }
    }

    public string Reason { get; }

    public string? SuggestedPath { get; }

    public bool HasSuggestion => SuggestedPath is not null;

    public string? ReplacementPath
    {
        get => _replacementPath;
        private set
        {
            if (SetProperty(ref _replacementPath, value))
            {
                OnPropertyChanged(nameof(IsResolved));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public bool IsResolved => !string.IsNullOrWhiteSpace(ReplacementPath);

    public string DetailText => IsResolved
        ? $"Relinked to {ReplacementPath}"
        : SuggestedPath is null
            ? $"{Reason} · {OriginalPath}"
            : $"{Reason} · Suggested: {SuggestedPath}";

    internal void Resolve(string replacementPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        ReplacementPath = Path.GetFullPath(replacementPath);
    }
}
