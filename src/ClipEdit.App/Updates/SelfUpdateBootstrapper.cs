using System.Diagnostics;

namespace ClipEdit.App.Updates;

internal static class SelfUpdateBootstrapper
{
    private const string ApplyFlag = "--clipedit-apply-update";
    private const string CleanupFlag = "--clipedit-cleanup-update";
    private const string ErrorFlag = "--clipedit-update-error";
    private const int MaximumErrorBytes = 32 * 1024;

    public static string? StartupError { get; private set; }

    public static bool TryRunUpdateHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], ApplyFlag, StringComparison.Ordinal))
        {
            return false;
        }

        exitCode = RunUpdateHelper(args);
        return true;
    }

    public static string[] PrepareApplicationArguments(string[] args)
    {
        var applicationArguments = new List<string>(args.Length);
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], CleanupFlag, StringComparison.Ordinal) && index + 1 < args.Length)
            {
                ScheduleCleanup(args[++index]);
                continue;
            }
            if (string.Equals(args[index], ErrorFlag, StringComparison.Ordinal) && index + 1 < args.Length)
            {
                StartupError = ReadError(args[++index]);
                continue;
            }
            applicationArguments.Add(args[index]);
        }

        return applicationArguments.ToArray();
    }

    public static bool CanReplaceCurrentExecutable()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
            Environment.ProcessPath is not { } processPath ||
            !File.Exists(processPath))
        {
            return false;
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("CLIPEDIT_ALLOW_SELF_UPDATE"),
                "1",
                StringComparison.Ordinal))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(processPath);
        var managedSidecar = directory is null
            ? null
            : Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(processPath)}.dll");
        return managedSidecar is not null && !File.Exists(managedSidecar);
    }

    public static void Launch(StagedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var targetPath = Environment.ProcessPath is { } processPath
            ? Path.GetFullPath(processPath)
            : throw new UpdateException("ClipEdit could not locate its running executable.");
        if (!CanReplaceCurrentExecutable())
        {
            throw new UpdateException("This build is not a portable single-file release and cannot replace itself.");
        }

        var stagedPath = Path.GetFullPath(update.ExecutablePath);
        var stagingDirectory = Path.GetFullPath(update.StagingDirectory);
        if (!File.Exists(stagedPath) ||
            !IsDirectChild(stagedPath, stagingDirectory))
        {
            throw new UpdateException("The staged update executable is missing or outside its update directory.");
        }

        EnsureTargetDirectoryWritable(targetPath);
        EnsureExecutable(stagedPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = stagedPath,
            WorkingDirectory = stagingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(ApplyFlag);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add(stagingDirectory);
        try
        {
            _ = Process.Start(startInfo) ??
                throw new UpdateException("The verified update helper could not be started.");
        }
        catch (Exception exception) when (exception is not UpdateException)
        {
            throw new UpdateException($"Could not start the update helper: {exception.Message}", exception);
        }
    }

    private static int RunUpdateHelper(string[] args)
    {
        string? targetPath = null;
        string? stagingDirectory = null;
        try
        {
            if (args.Length != 4 ||
                !int.TryParse(
                    args[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var oldProcessId) ||
                oldProcessId <= 0)
            {
                return 2;
            }

            targetPath = Path.GetFullPath(args[2]);
            stagingDirectory = Path.GetFullPath(args[3]);
            var helperPath = Environment.ProcessPath is { } currentPath
                ? Path.GetFullPath(currentPath)
                : throw new UpdateException("The update helper could not locate itself.");
            if (!IsDirectChild(helperPath, stagingDirectory))
            {
                throw new UpdateException("The update helper is outside its staging directory.");
            }

            WaitForProcessExit(oldProcessId);
            ReplaceExecutable(helperPath, targetPath);
            StartUpdatedApplication(targetPath, stagingDirectory, errorPath: null);
            return 0;
        }
        catch (Exception exception)
        {
            if (targetPath is not null && stagingDirectory is not null)
            {
                var errorPath = TryWriteError(stagingDirectory, exception);
                try
                {
                    if (File.Exists(targetPath))
                    {
                        StartUpdatedApplication(targetPath, stagingDirectory, errorPath);
                    }
                }
                catch (Exception restartException)
                {
                    _ = TryWriteError(
                        stagingDirectory,
                        new AggregateException(exception, restartException));
                }
            }
            return 1;
        }
    }

    private static void WaitForProcessExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            {
                throw new UpdateException("ClipEdit did not exit before the update timed out.");
            }
        }
        catch (ArgumentException)
        {
            // The original process exited before the helper inspected it.
        }
    }

    private static void ReplaceExecutable(string sourcePath, string targetPath)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath) ??
                              throw new UpdateException("The application directory is invalid.");
        var replacementPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.updating");
        try
        {
            File.Copy(sourcePath, replacementPath, overwrite: false);
            EnsureExecutable(replacementPath);
            Exception? finalException = null;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                try
                {
                    File.Move(replacementPath, targetPath, overwrite: true);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    finalException = exception;
                    Thread.Sleep(250);
                }
            }

            throw new UpdateException(
                "The old ClipEdit executable remained locked and could not be replaced.",
                finalException!);
        }
        finally
        {
            TryDeleteFile(replacementPath);
        }
    }

    private static void StartUpdatedApplication(
        string targetPath,
        string stagingDirectory,
        string? errorPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = targetPath,
            WorkingDirectory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(CleanupFlag);
        startInfo.ArgumentList.Add(stagingDirectory);
        if (errorPath is not null)
        {
            startInfo.ArgumentList.Add(ErrorFlag);
            startInfo.ArgumentList.Add(errorPath);
        }
        _ = Process.Start(startInfo) ?? throw new UpdateException("Updated ClipEdit could not be restarted.");
    }

    private static void EnsureTargetDirectoryWritable(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ??
                        throw new UpdateException("The application directory is invalid.");
        var probePath = Path.Combine(directory, $".clipedit-update-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(
                "ClipEdit cannot update this executable because its folder is not writable. Move the portable app to a user-writable folder or update it manually.",
                exception);
        }
        finally
        {
            TryDeleteFile(probePath);
        }
    }

    private static void ScheduleCleanup(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return;
        }

        if (!IsWithinUpdatesRoot(fullPath))
        {
            return;
        }

        var cleanupThread = new Thread(() =>
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    if (!Directory.Exists(fullPath))
                    {
                        return;
                    }
                    Directory.Delete(fullPath, recursive: true);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(500);
                }
            }
        })
        {
            IsBackground = true,
            Name = "ClipEdit update cleanup",
        };
        cleanupThread.Start();
    }

    private static string? ReadError(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaximumErrorBytes
                ? $"The update could not be installed: {File.ReadAllText(path).Trim()}"
                : "The update could not be installed; the previous ClipEdit version was restarted.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "The update could not be installed; the previous ClipEdit version was restarted.";
        }
    }

    private static string? TryWriteError(string stagingDirectory, Exception exception)
    {
        try
        {
            var errorPath = Path.Combine(stagingDirectory, "update-error.txt");
            File.WriteAllText(errorPath, exception.Message);
            return errorPath;
        }
        catch (Exception writeException) when (
            writeException is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsWithinUpdatesRoot(string path)
    {
        var root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipEdit",
            "Updates"));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsDirectChild(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
            comparison);
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
