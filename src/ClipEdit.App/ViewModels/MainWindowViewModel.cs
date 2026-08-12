namespace ClipEdit.App.ViewModels;

public sealed class MainWindowViewModel
{
    public string ProductName => "ClipEdit";

    public string WorkspaceTitle => "Create a short clip";

    public string EmptyStateTitle => "Drop a video to begin";

    public string EmptyStateDescription =>
        "Your source stays untouched. ClipEdit will reveal trimming and crop controls after import.";

    public string SupportedMediaHint => "Video and audio files supported by the local media engine";
}
