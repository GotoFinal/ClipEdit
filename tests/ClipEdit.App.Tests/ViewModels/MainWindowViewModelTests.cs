using System.Collections.Immutable;
using ClipEdit.App.ViewModels;
using ClipEdit.Application.Export;
using ClipEdit.Application.Projects;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.Probe;
using ClipEdit.Persistence.Json;

namespace ClipEdit.App.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task Progressive_workspace_reveals_only_when_content_requires_it()
    {
        var firstVideo = Path.Combine(Path.GetTempPath(), "first.mkv");
        var secondVideo = Path.Combine(Path.GetTempPath(), "second.mp4");
        var music = Path.Combine(Path.GetTempPath(), "music.flac");
        var viewModel = new MainWindowViewModel(new StubProbe());

        await viewModel.ImportFilesAsync([firstVideo]);

        Assert.True(viewModel.ShowQuickWorkspace);
        Assert.True(viewModel.ShowRangeStrip);
        Assert.False(viewModel.ShowTimeline);
        Assert.False(viewModel.ShowAudioMixer);
        Assert.Equal(new PixelSize(1_920, 1_080), viewModel.SelectedMedia!.Crop.ExportSize);

        await viewModel.ImportFilesAsync([secondVideo]);

        Assert.True(viewModel.ShowTimeline);
        Assert.False(viewModel.ShowRangeStrip);
        Assert.False(viewModel.ShowAudioMixer);

        await viewModel.ImportFilesAsync([music]);

        Assert.True(viewModel.ShowTimeline);
        Assert.True(viewModel.ShowAudioMixer);
        Assert.Equal(2, viewModel.VideoItems.Count());
        Assert.Single(viewModel.ExternalAudioItems);
    }

    [Fact]
    public async Task Import_ignores_duplicate_paths()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "same.mkv");
        var viewModel = new MainWindowViewModel(new StubProbe());

        await viewModel.ImportFilesAsync([sourcePath, sourcePath]);
        await viewModel.ImportFilesAsync([sourcePath]);

        Assert.Single(viewModel.MediaItems);
    }

    [Fact]
    public async Task Import_failure_stays_local_to_the_failed_media_item()
    {
        var goodPath = Path.Combine(Path.GetTempPath(), "good.mkv");
        var badPath = Path.Combine(Path.GetTempPath(), "bad.mkv");
        var viewModel = new MainWindowViewModel(new StubProbe(badPath));

        await viewModel.ImportFilesAsync([badPath, goodPath]);

        Assert.Equal(2, viewModel.MediaItems.Count);
        Assert.True(viewModel.MediaItems[0].HasError);
        Assert.True(viewModel.MediaItems[1].IsReady);
        Assert.True(viewModel.ShowQuickWorkspace);
    }

    [Fact]
    public async Task Selected_source_can_remove_an_exact_quantized_range_and_reset_it()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "cut.mkv");
        var viewModel = new MainWindowViewModel(new StubProbe());

        await viewModel.ImportFilesAsync([sourcePath]);
        var media = viewModel.SelectedMedia!;
        media.PlayheadSeconds = 10.1234;
        media.MarkSelectionStart();
        media.PlayheadSeconds = 20.5678;
        media.MarkSelectionEnd();

        Assert.True(media.RemoveSelection());
        Assert.Equal(new MediaTime(49_555, 1_000), media.Edit!.OutputDuration);
        Assert.Equal<MediaRange>(
            [
                new MediaRange(MediaTime.Zero, new MediaTime(10_123, 1_000)),
                new MediaRange(new MediaTime(20_568, 1_000), new MediaTime(60, 1)),
            ],
            media.KeptRanges);

        media.ResetCuts();

        Assert.True(media.Edit.IsUnedited);
        Assert.Equal(new MediaTime(60, 1), media.SelectionEnd);
    }

    [Fact]
    public async Task Export_uses_the_selected_preset_and_current_exact_edits()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "source.mkv")]);
        var media = viewModel.SelectedMedia!;
        media.PlayheadSeconds = 5;
        media.MarkSelectionStart();
        media.PlayheadSeconds = 10;
        media.MarkSelectionEnd();
        media.RemoveSelection();
        Assert.Single(viewModel.AudioTracks).GainDb = -3;
        viewModel.SelectedExportPreset = BuiltInExportPresets.WebM;
        var destination = Path.Combine(Path.GetTempPath(), "rendered clip.webm");

        var result = await viewModel.ExportAsync(destination, replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(BuiltInExportPresets.WebM, renderer.Plan!.Preset);
        Assert.Equal(media.KeptRanges, renderer.Plan.SourceRanges);
        Assert.Equal(media.Crop, renderer.Plan.Crop);
        Assert.Equal(-3, Assert.Single(renderer.Plan.AudioTracks).GainDb);
        Assert.Equal(destination, renderer.Plan.DestinationPath);
        Assert.Equal("source-clip.webm", viewModel.GetSuggestedExportFileName());
    }

    [Fact]
    public async Task Embedded_audio_mixer_state_is_projected_for_live_preview()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "source.mkv")]);
        var track = Assert.Single(viewModel.AudioTracks);

        track.GainDb = -7.5;
        track.IsMuted = true;

        var previewTrack = Assert.Single(viewModel.PreviewAudioTracks);
        Assert.Equal(track.StreamIndex, previewTrack.StreamIndex);
        Assert.Equal(-7.5, previewTrack.GainDb);
        Assert.True(previewTrack.IsMuted);
        Assert.False(previewTrack.IsExternal);
    }

    [Fact]
    public async Task External_audio_is_projected_to_preview_and_exact_export_from_time_zero()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        var videoPath = Path.Combine(Path.GetTempPath(), "source.mkv");
        var musicPath = Path.Combine(Path.GetTempPath(), "music.flac");
        await viewModel.ImportFilesAsync([videoPath, musicPath]);
        var externalTrack = Assert.Single(viewModel.AudioTracks, track => track.IsExternal);
        externalTrack.GainDb = -8;

        var previewTrack = Assert.Single(viewModel.PreviewAudioTracks, track => track.IsExternal);
        Assert.Equal(musicPath, previewTrack.ExternalSourcePath);
        Assert.Equal(-8, previewTrack.GainDb);
        Assert.True(viewModel.CanExport);

        await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "mixed.mp4"),
            replaceExistingDestination: false);

        var exportedTrack = Assert.Single(renderer.Plan!.AudioTracks, track => track.IsExternal);
        Assert.Equal(musicPath, exportedTrack.ExternalSourcePath);
        Assert.Equal(-8, exportedTrack.GainDb);
    }

    [Fact]
    public async Task Export_is_disabled_with_an_actionable_reason_for_odd_dimensions()
    {
        var viewModel = new MainWindowViewModel(
            new StubProbe(),
            exportRenderer: new RecordingExportRenderer());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "source.mkv")]);

        viewModel.SelectedMedia!.CropWidth = 1_919;

        Assert.False(viewModel.CanExport);
        Assert.Contains("even", viewModel.ExportAvailabilityText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new PixelSize(1_919, 1_080), viewModel.SelectedMedia.Crop.ExportSize);
    }

    [Fact]
    public async Task Project_round_trip_restores_exact_cuts_crop_and_export_preset()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), $"clipedit-{Guid.NewGuid():N}.clipedit");
        var sourcePath = Path.Combine(Path.GetTempPath(), "saved source.mkv");
        var store = new JsonProjectStore();
        using var original = new MainWindowViewModel(new StubProbe(), projectStore: store);

        try
        {
            await original.ImportFilesAsync([sourcePath]);
            var media = original.SelectedMedia!;
            media.Crop = new CropRegion(media.VideoSize, 420, 0, 1_080, 1_080);
            media.PlayheadSeconds = 5;
            media.MarkSelectionStart();
            media.PlayheadSeconds = 10;
            media.MarkSelectionEnd();
            media.RemoveSelection();
            var audioTrack = Assert.Single(original.AudioTracks);
            audioTrack.SelectionStartSeconds = 12;
            audioTrack.SelectionEndSeconds = 14;
            audioTrack.RemoveSelection();
            audioTrack.GainDb = -4.5;
            audioTrack.IsMuted = true;
            original.SelectedExportPreset = BuiltInExportPresets.WebM;
            Assert.True(await original.SaveProjectAsync(projectPath));
            Assert.False(original.IsProjectDirty);

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await restored.OpenProjectAsync(projectPath));

            Assert.Equal(BuiltInExportPresets.WebM, restored.SelectedExportPreset);
            Assert.Equal(media.Crop, restored.SelectedMedia!.Crop);
            Assert.Equal(media.Edit!.SourceDuration, restored.SelectedMedia.Edit!.SourceDuration);
            Assert.Equal<MediaRange>(media.KeptRanges, restored.SelectedMedia.KeptRanges);
            var restoredAudio = Assert.Single(restored.AudioTracks);
            Assert.Equal(audioTrack.GainDb, restoredAudio.GainDb);
            Assert.Equal(audioTrack.IsMuted, restoredAudio.IsMuted);
            Assert.Equal<MediaRange>(audioTrack.KeptRanges, restoredAudio.KeptRanges);
            Assert.False(restored.IsProjectDirty);

            using var recovered = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await recovered.RecoverProjectAsync(projectPath));
            Assert.Null(recovered.ProjectPath);
            Assert.True(recovered.IsProjectDirty);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task Dirty_edits_are_coalesced_into_a_recovery_autosave()
    {
        var store = new RecordingProjectStore();
        using var viewModel = new MainWindowViewModel(
            new StubProbe(),
            projectStore: store,
            recoveryDirectory: Path.GetTempPath(),
            autosaveDelay: TimeSpan.FromMilliseconds(10));

        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "autosave.mkv")]);
        var document = await store.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsProjectDirty);
        Assert.Single(document.Media);
        Assert.EndsWith(".recovery.clipedit", store.SavedPath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubProbe(string? failingPath = null) : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(sourcePath, failingPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new MediaProbeException(MediaProbeFailure.ToolFailed, "The fixture is intentionally bad.");
            }

            var extension = Path.GetExtension(sourcePath);
            return Task.FromResult(
                string.Equals(extension, ".flac", StringComparison.OrdinalIgnoreCase)
                    ? CreateAudioProbe(sourcePath)
                    : CreateVideoProbe(sourcePath));
        }
    }

    private sealed class RecordingExportRenderer : IExportRenderer
    {
        public ExportPlan? Plan { get; private set; }

        public Task<ExportResult> RenderAsync(
            ExportPlan plan,
            IProgress<ExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plan = plan;
            progress?.Report(new ExportProgress(1, "Complete", TimeSpan.FromSeconds(1)));
            return Task.FromResult(new ExportResult(plan.DestinationPath, 1_024, TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class RecordingProjectStore : IProjectStore
    {
        public TaskCompletionSource<ProjectDocument> Saved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? SavedPath { get; private set; }

        public Task<ProjectDocument> LoadAsync(
            string projectPath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveAsync(
            string projectPath,
            ProjectDocument document,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedPath = projectPath;
            Saved.TrySetResult(document);
            return Task.CompletedTask;
        }

        public Task DeleteIfExistsAsync(
            string projectPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static MediaProbeResult CreateVideoProbe(string sourcePath)
    {
        return CreateProbe(
            sourcePath,
            new VideoStreamInfo(
                0,
                "h264",
                null,
                null,
                null,
                null,
                true,
                false,
                new MediaTime(1, 1_000),
                MediaTime.Zero,
                new MediaTime(60, 1),
                new PixelSize(1_920, 1_080),
                0,
                new FrameRate(24_000, 1_001),
                new FrameRate(24_000, 1_001),
                "yuv420p",
                "1:1",
                "16:9",
                "tv",
                "bt709",
                "bt709",
                "bt709",
                "progressive"),
            CreateAudioStream());
    }

    private static MediaProbeResult CreateAudioProbe(string sourcePath)
    {
        return CreateProbe(sourcePath, CreateAudioStream());
    }

    private static AudioStreamInfo CreateAudioStream()
    {
        return new AudioStreamInfo(
            1,
            "flac",
            null,
            null,
            null,
            null,
            true,
            false,
            new MediaTime(1, 1_000),
            MediaTime.Zero,
            new MediaTime(60, 1),
            48_000,
            2,
            "stereo",
            "s32");
    }

    private static MediaProbeResult CreateProbe(
        string sourcePath,
        params MediaStreamInfo[] streams)
    {
        return new MediaProbeResult(
            sourcePath,
            "fixture",
            "Test fixture",
            MediaTime.Zero,
            new MediaTime(60, 1),
            1_024,
            8_000,
            streams.ToImmutableArray());
    }
}
