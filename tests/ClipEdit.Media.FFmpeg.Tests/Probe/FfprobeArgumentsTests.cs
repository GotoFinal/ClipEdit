using ClipEdit.Media.FFmpeg.Probe;

namespace ClipEdit.Media.FFmpeg.Tests.Probe;

public sealed class FfprobeArgumentsTests
{
    [Fact]
    public void Source_path_is_a_single_argument_after_the_option_terminator()
    {
        const string sourcePath = "C:\\media\\a file & whoami $(bad).mkv";

        var arguments = FfprobeArguments.Create(sourcePath);

        Assert.Equal("--", arguments[^2]);
        Assert.Equal(sourcePath, arguments[^1]);
        Assert.DoesNotContain($"\"{sourcePath}\"", arguments);
    }
}
