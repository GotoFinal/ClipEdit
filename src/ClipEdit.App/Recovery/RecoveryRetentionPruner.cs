namespace ClipEdit.App.Recovery;

internal readonly record struct RecoveryPruneResult(
    int DeletedRecoveryFiles,
    int DeletedTemporaryFiles,
    int FailedFiles)
{
    public int DeletedFiles => DeletedRecoveryFiles + DeletedTemporaryFiles;
}

internal static class RecoveryRetentionPruner
{
    private const string RecoveryPattern = "*.recovery.clipedit";
    private const string TemporaryRecoveryPattern = "*.recovery.clipedit.*.saving";

    public static RecoveryPruneResult Prune(
        string recoveryDirectory,
        int retentionDays,
        int maximumRecoveryFiles,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryDirectory);
        if (retentionDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        if (maximumRecoveryFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecoveryFiles));
        }

        if (!Directory.Exists(recoveryDirectory))
        {
            return default;
        }

        var cutoffUtc = now.UtcDateTime.AddDays(-retentionDays);
        var failedFiles = 0;
        var deletedRecoveryFiles = 0;
        var deletedTemporaryFiles = 0;
        var recoveryFiles = new List<RecoveryFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         recoveryDirectory,
                         RecoveryPattern,
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    recoveryFiles.Add(new RecoveryFile(path, File.GetLastWriteTimeUtc(path)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failedFiles++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RecoveryPruneResult(0, 0, 1);
        }

        recoveryFiles.Sort(static (left, right) =>
        {
            var timeComparison = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
            return timeComparison != 0
                ? timeComparison
                : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });

        var retainedFiles = new List<RecoveryFile>(recoveryFiles.Count);
        foreach (var file in recoveryFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc < cutoffUtc)
            {
                if (TryDelete(file.Path))
                {
                    deletedRecoveryFiles++;
                }
                else
                {
                    failedFiles++;
                }

                continue;
            }

            retainedFiles.Add(file);
        }

        foreach (var file in retainedFiles.Skip(maximumRecoveryFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDelete(file.Path))
            {
                deletedRecoveryFiles++;
            }
            else
            {
                failedFiles++;
            }
        }

        try
        {
            foreach (var temporaryPath in Directory.EnumerateFiles(
                         recoveryDirectory,
                         TemporaryRecoveryPattern,
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.GetLastWriteTimeUtc(temporaryPath) >= cutoffUtc)
                    {
                        continue;
                    }

                    if (TryDelete(temporaryPath))
                    {
                        deletedTemporaryFiles++;
                    }
                    else
                    {
                        failedFiles++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failedFiles++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failedFiles++;
        }

        return new RecoveryPruneResult(
            deletedRecoveryFiles,
            deletedTemporaryFiles,
            failedFiles);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly record struct RecoveryFile(string Path, DateTime LastWriteTimeUtc);
}
