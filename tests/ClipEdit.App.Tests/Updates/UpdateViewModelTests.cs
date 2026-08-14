using ClipEdit.App.Updates;

namespace ClipEdit.App.Tests.Updates;

public sealed class UpdateViewModelTests
{
    [Theory]
    [InlineData(null, "linux-x64", "linux-x64")]
    [InlineData("linux-x64", "linux-x64", "linux-x64")]
    [InlineData("linux-x64-system", "linux-x64", "linux-x64-system")]
    [InlineData("linux-x64-system", "win-x64", "win-x64")]
    [InlineData("unknown", "linux-x64", "linux-x64")]
    public void Release_identity_preserves_only_a_compatible_packaged_variant(
        string? configuredAssetId,
        string runtimeId,
        string expectedAssetId)
    {
        Assert.Equal(
            expectedAssetId,
            UpdateViewModel.ResolveReleaseAssetId(configuredAssetId, runtimeId));
    }

    [Fact]
    public async Task Beta_toggle_is_passed_to_GitHub_check_and_exposes_verified_update_action()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipedit-update-vm-{Guid.NewGuid():N}");
        try
        {
            Assert.True(SemanticVersion.TryParse("1.0.0", out var current));
            Assert.True(SemanticVersion.TryParse("1.1.0-beta.1", out var betaVersion));
            var release = new AvailableUpdate(
                betaVersion,
                "v1.1.0-beta.1",
                "ClipEdit beta",
                new Uri("https://github.com/GotoFinal/ClipEdit/releases/tag/v1.1.0-beta.1"),
                DateTimeOffset.UtcNow,
                true,
                new UpdateAsset(
                    "ClipEdit-win-x64.exe",
                    new Uri("https://github.com/GotoFinal/ClipEdit/releases/download/v1.1.0-beta.1/ClipEdit-win-x64.exe"),
                    100,
                    new string('a', 64),
                    null));
            var client = new RecordingUpdateClient(release);
            using var viewModel = new UpdateViewModel(
                client,
                new UpdateSettingsStore(Path.Combine(directory, "updates.json")),
                "win-x64",
                Path.Combine(directory, "staging"),
                current,
                canSelfUpdate: true);

            viewModel.IncludeBetaVersions = true;
            await viewModel.CheckNowAsync();

            Assert.True(client.LastIncludePrereleases);
            Assert.True(viewModel.HasAvailableUpdate);
            Assert.True(viewModel.ShowUpdateButton);
            Assert.True(viewModel.CanApplyUpdate);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class RecordingUpdateClient(AvailableUpdate update) : IUpdateClient
    {
        public bool LastIncludePrereleases { get; private set; }

        public Task<AvailableUpdate?> CheckAsync(
            SemanticVersion currentVersion,
            string releaseAssetId,
            bool includePrereleases,
            CancellationToken cancellationToken)
        {
            LastIncludePrereleases = includePrereleases;
            return Task.FromResult<AvailableUpdate?>(update);
        }

        public Task<StagedUpdate> DownloadAsync(
            AvailableUpdate release,
            string stagingRoot,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
