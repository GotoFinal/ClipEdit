namespace ClipEdit.App.Tests;

public sealed class AppStartupTests
{
    [Fact]
    public void Empty_startup_arguments_do_not_open_implicit_content()
    {
        var startupArguments = App.ClassifyStartupArguments([]);

        Assert.Null(startupArguments.ProjectPath);
        Assert.Empty(startupArguments.MediaPaths);
    }

    [Fact]
    public void Startup_arguments_only_include_existing_explicit_paths()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("clipedit-startup-");
        try
        {
            var projectPath = Path.Combine(temporaryDirectory.FullName, "project.clipedit");
            var mediaPath = Path.Combine(temporaryDirectory.FullName, "video.mkv");
            File.WriteAllText(projectPath, "{}");
            File.WriteAllText(mediaPath, string.Empty);

            var startupArguments = App.ClassifyStartupArguments(
            [
                Path.Combine(temporaryDirectory.FullName, "missing.mp4"),
                mediaPath,
                projectPath,
            ]);

            Assert.Equal(Path.GetFullPath(projectPath), startupArguments.ProjectPath);
            Assert.Equal([Path.GetFullPath(mediaPath)], startupArguments.MediaPaths);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Project_path_is_classified_as_a_project_instead_of_media()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("clipedit-drop-");
        try
        {
            var projectPath = Path.Combine(temporaryDirectory.FullName, "project.CLIPEDIT");
            var mediaPath = Path.Combine(temporaryDirectory.FullName, "video.mp4");
            File.WriteAllText(projectPath, "{}");
            File.WriteAllText(mediaPath, string.Empty);

            var selection = InputPathClassifier.Classify([mediaPath, projectPath]);

            Assert.Equal(Path.GetFullPath(projectPath), selection.ProjectPath);
            Assert.Equal([Path.GetFullPath(mediaPath)], selection.MediaPaths);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }
}
