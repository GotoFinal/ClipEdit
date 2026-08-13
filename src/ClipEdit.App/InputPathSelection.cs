namespace ClipEdit.App;

internal static class InputPathClassifier
{
    public static InputPathSelection Classify(IEnumerable<string>? paths)
    {
        var existingPaths = paths?
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToArray() ?? [];
        var projectPath = existingPaths.FirstOrDefault(IsProjectPath);
        var mediaPaths = existingPaths
            .Where(path => !IsProjectPath(path))
            .ToArray();

        return new InputPathSelection(projectPath, mediaPaths);
    }

    private static bool IsProjectPath(string path) =>
        string.Equals(
            Path.GetExtension(path),
            ".clipedit",
            StringComparison.OrdinalIgnoreCase);
}

internal sealed record InputPathSelection(string? ProjectPath, IReadOnlyList<string> MediaPaths);
