using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClipEdit.App.Platform;

internal sealed class WindowsProjectFileAssociationService : IProjectFileAssociationService
{
    internal const string ProjectExtension = ".clipedit";
    internal const string ProjectProgramId = "ClipEdit.Project";

    private readonly string _executablePath;

    public WindowsProjectFileAssociationService(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        if (_executablePath.Contains('"'))
        {
            throw new ArgumentException("The executable path cannot contain a quote.", nameof(executablePath));
        }
    }

    public ProjectFileAssociationResult Register()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProjectFileAssociationResult(
                false,
                "File association setup is available on Windows only.");
        }

        try
        {
            RegisterForCurrentWindowsUser();
            return new ProjectFileAssociationResult(
                true,
                "ClipEdit is registered for .clipedit files. Run this again if you move the portable app.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ProjectFileAssociationResult(
                false,
                $"Windows could not register .clipedit files: {exception.Message}");
        }
    }

    internal static string BuildOpenCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"'))
        {
            throw new ArgumentException("The executable path cannot contain a quote.", nameof(executablePath));
        }

        return $"\"{Path.GetFullPath(executablePath)}\" \"%1\"";
    }

    [SupportedOSPlatform("windows")]
    private void RegisterForCurrentWindowsUser()
    {
        var executableName = Path.GetFileName(_executablePath);
        var openCommand = BuildOpenCommand(_executablePath);

        using (var extension = Registry.CurrentUser.CreateSubKey(
                   $"Software\\Classes\\{ProjectExtension}", writable: true))
        {
            extension.SetValue(string.Empty, ProjectProgramId, RegistryValueKind.String);
            using var openWith = extension.CreateSubKey("OpenWithProgids", writable: true);
            openWith.SetValue(ProjectProgramId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var projectType = Registry.CurrentUser.CreateSubKey(
                   $"Software\\Classes\\{ProjectProgramId}", writable: true))
        {
            projectType.SetValue(string.Empty, "ClipEdit project", RegistryValueKind.String);
            projectType.SetValue("FriendlyTypeName", "ClipEdit project", RegistryValueKind.String);
            using var icon = projectType.CreateSubKey("DefaultIcon", writable: true);
            icon.SetValue(string.Empty, $"\"{_executablePath}\",0", RegistryValueKind.String);
            using var command = projectType.CreateSubKey("shell\\open\\command", writable: true);
            command.SetValue(string.Empty, openCommand, RegistryValueKind.String);
        }

        using (var application = Registry.CurrentUser.CreateSubKey(
                   $"Software\\Classes\\Applications\\{executableName}", writable: true))
        {
            application.SetValue("FriendlyAppName", "ClipEdit", RegistryValueKind.String);
            using var command = application.CreateSubKey("shell\\open\\command", writable: true);
            command.SetValue(string.Empty, openCommand, RegistryValueKind.String);
            using var supportedTypes = application.CreateSubKey("SupportedTypes", writable: true);
            supportedTypes.SetValue(ProjectExtension, string.Empty, RegistryValueKind.String);
        }

        SHChangeNotify(
            eventId: 0x08000000,
            flags: 0x0000,
            item1: nint.Zero,
            item2: nint.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
