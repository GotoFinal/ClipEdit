using ClipEdit.App.Recovery;

namespace ClipEdit.App.Tests.Recovery;

public sealed class RecoveryRetentionPrunerTests
{
    [Fact]
    public void Prune_keeps_only_the_twenty_newest_recoveries_from_the_last_seven_days()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-recovery-retention-");
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        try
        {
            var oldPaths = new[]
            {
                CreateFile(directory.FullName, "old-1.recovery.clipedit", now.AddDays(-8)),
                CreateFile(directory.FullName, "old-2.recovery.clipedit", now.AddDays(-30)),
            };
            var recentPaths = Enumerable.Range(0, 22)
                .Select(index => CreateFile(
                    directory.FullName,
                    $"recent-{index:D2}.recovery.clipedit",
                    now.AddHours(-index)))
                .ToArray();
            var unrelatedPath = CreateFile(
                directory.FullName,
                "ordinary-project.clipedit",
                now.AddDays(-100));

            var result = RecoveryRetentionPruner.Prune(
                directory.FullName,
                retentionDays: 7,
                maximumRecoveryFiles: 20,
                now);

            Assert.Equal(4, result.DeletedRecoveryFiles);
            Assert.Equal(0, result.DeletedTemporaryFiles);
            Assert.Equal(0, result.FailedFiles);
            Assert.All(oldPaths, path => Assert.False(File.Exists(path)));
            Assert.All(recentPaths.Take(20), path => Assert.True(File.Exists(path)));
            Assert.All(recentPaths.Skip(20), path => Assert.False(File.Exists(path)));
            Assert.True(File.Exists(unrelatedPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Prune_removes_only_expired_interrupted_save_files()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-recovery-temporary-");
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        try
        {
            var expiredPath = CreateFile(
                directory.FullName,
                ".project.recovery.clipedit.old.saving",
                now.AddDays(-8));
            var recentPath = CreateFile(
                directory.FullName,
                ".project.recovery.clipedit.recent.saving",
                now.AddDays(-1));

            var result = RecoveryRetentionPruner.Prune(
                directory.FullName,
                retentionDays: 7,
                maximumRecoveryFiles: 20,
                now);

            Assert.Equal(0, result.DeletedRecoveryFiles);
            Assert.Equal(1, result.DeletedTemporaryFiles);
            Assert.Equal(0, result.FailedFiles);
            Assert.False(File.Exists(expiredPath));
            Assert.True(File.Exists(recentPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string CreateFile(
        string directory,
        string fileName,
        DateTimeOffset lastWriteTime)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "recovery");
        File.SetLastWriteTimeUtc(path, lastWriteTime.UtcDateTime);
        return path;
    }
}
