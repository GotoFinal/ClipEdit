using ClipEdit.App.Updates;

namespace ClipEdit.App.Tests.Updates;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+build.4", "1.2.3")]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.2")]
    public void Parses_release_tags(string input, string normalized)
    {
        Assert.True(SemanticVersion.TryParse(input, out var version));
        Assert.Equal(normalized, version.ToString());
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0-beta.9")]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2")]
    [InlineData("2.0.0", "1.99.99")]
    public void Orders_versions_by_semantic_version_rules(string newer, string older)
    {
        Assert.True(SemanticVersion.TryParse(newer, out var newerVersion));
        Assert.True(SemanticVersion.TryParse(older, out var olderVersion));
        Assert.True(newerVersion > olderVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.02.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta.01")]
    [InlineData("1.2.3+")]
    [InlineData("latest")]
    public void Rejects_non_semantic_release_tags(string input)
    {
        Assert.False(SemanticVersion.TryParse(input, out _));
    }
}
