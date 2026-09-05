using ClipEdit.App.Diagnostics;

namespace ClipEdit.App.Tests.Diagnostics;

public sealed class SessionLogTests
{
    [Fact]
    public void A_new_launch_replaces_previous_log_and_concurrent_commands_are_readable()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-log-test-");
        var path = Path.Combine(directory.FullName, "session.log");
        try
        {
            using (var previous = new SessionLog(path))
            {
                previous.Write("previous session");
            }
            using (var current = new SessionLog(path))
            {
                Parallel.For(0, 100, index => current.Write($"command-{index:D3}"));
                using var reader = new StreamReader(new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                var text = reader.ReadToEnd();
                Assert.DoesNotContain("previous session", text);
                Assert.Equal(100, text.Split('\n').Count(line => line.Contains("command-")));
                for (var index = 0; index < 100; index++)
                {
                    Assert.Contains($"command-{index:D3}", text);
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Unavailable_log_location_does_not_prevent_startup()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-log-unavailable-");
        try
        {
            using var log = new SessionLog(directory.FullName);
            log.Write("Still running");
        }
        finally
        {
            directory.Delete();
        }
    }
}
