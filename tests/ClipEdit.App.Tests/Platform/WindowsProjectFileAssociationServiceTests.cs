using ClipEdit.App.Platform;

namespace ClipEdit.App.Tests.Platform;

public sealed class WindowsProjectFileAssociationServiceTests
{
    [Fact]
    public void Open_command_quotes_the_portable_executable_and_project_path()
    {
        var executablePath = Path.Combine(
            Path.GetTempPath(),
            "ClipEdit portable",
            "ClipEdit.exe");

        var command = WindowsProjectFileAssociationService.BuildOpenCommand(executablePath);

        Assert.Equal($"\"{Path.GetFullPath(executablePath)}\" \"%1\"", command);
    }

    [Fact]
    public void Open_command_rejects_a_path_that_could_inject_arguments()
    {
        Assert.Throws<ArgumentException>(() =>
            WindowsProjectFileAssociationService.BuildOpenCommand("C:\\Bad\"Path\\ClipEdit.exe"));
    }
}
