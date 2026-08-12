using System.Collections.Immutable;
using Avalonia.Headless.XUnit;
using ClipEdit.App.ViewModels;
using ClipEdit.Application.Export;
using ClipEdit.Application.Projects;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Analysis;
using ClipEdit.Media.Export;
using ClipEdit.Media.Frames;
using ClipEdit.Media.Probe;
using ClipEdit.Persistence.Json;

namespace ClipEdit.App.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

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
    public async Task New_project_requires_discard_confirmation_and_clears_all_editing_state()
    {
        var store = new RecordingProjectStore();
        using var viewModel = new MainWindowViewModel(
            new StubProbe(),
            projectStore: store,
            recoveryDirectory: Path.GetTempPath(),
            autosaveDelay: TimeSpan.FromHours(1));
        await viewModel.ImportFilesAsync(
        [
            Path.Combine(Path.GetTempPath(), "new-project.mkv"),
            Path.Combine(Path.GetTempPath(), "new-project.flac"),
        ]);
        var previousProjectId = viewModel.CreateProjectDocument().ProjectId;

        Assert.False(await viewModel.NewProjectAsync());
        Assert.NotEmpty(viewModel.MediaItems);

        Assert.True(await viewModel.NewProjectAsync(discardUnsavedChanges: true));
        Assert.Empty(viewModel.MediaItems);
        Assert.Empty(viewModel.AudioTracks);
        Assert.Null(viewModel.SelectedMedia);
        Assert.Null(viewModel.ProjectPath);
        Assert.False(viewModel.IsProjectDirty);
        Assert.False(viewModel.ShowQuickWorkspace);
        Assert.NotEqual(previousProjectId, viewModel.CreateProjectDocument().ProjectId);
        Assert.EndsWith($"{previousProjectId:N}.recovery.clipedit", store.DeletedPath);
    }

    [Fact]
    public async Task Removing_selected_media_removes_its_tracks_but_never_its_source()
    {
        var videoPath = Path.Combine(Path.GetTempPath(), "remove.mkv");
        var musicPath = Path.Combine(Path.GetTempPath(), "keep.flac");
        using var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([videoPath, musicPath]);

        Assert.True(viewModel.RemoveSelectedMedia());

        Assert.DoesNotContain(viewModel.MediaItems, item => item.SourcePath == videoPath);
        Assert.DoesNotContain(viewModel.AudioTracks, track => track.SourcePath == videoPath);
        Assert.Contains(viewModel.MediaItems, item => item.SourcePath == musicPath);
        Assert.True(viewModel.IsProjectDirty);

        await viewModel.ImportFilesAsync([videoPath]);
        Assert.Contains(viewModel.MediaItems, item => item.SourcePath == videoPath);
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
        Assert.Equal(media.SelectionStart, media.SelectionEnd);
        Assert.Equal(new MediaTime(20_568, 1_000), media.SelectionStart);

        media.ResetCuts();

        Assert.True(media.Edit.IsUnedited);
        Assert.Equal(new MediaTime(60, 1), media.SelectionEnd);
    }

    [Fact]
    public async Task Selected_source_crop_can_reset_to_the_full_oriented_frame()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "crop-reset.mkv")]);
        var media = viewModel.SelectedMedia!;
        media.Crop = new CropRegion(media.VideoSize, 420, 0, 1_080, 1_080);

        media.ResetCrop();

        Assert.Equal(CropRegion.FullFrame(media.VideoSize), media.Crop);
    }

    [Fact]
    public async Task Selected_source_can_keep_only_the_active_range()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "keep-only.mkv")]);
        var media = viewModel.SelectedMedia!;
        media.SelectionStartSeconds = 5;
        media.SelectionEndSeconds = 12;

        Assert.True(media.KeepSelectionOnly());

        Assert.Equal(
            new MediaRange(new MediaTime(5, 1), new MediaTime(12, 1)),
            Assert.Single(media.KeptRanges));
        Assert.Equal(new MediaTime(7, 1), media.Edit!.OutputDuration);
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
    public async Task Export_automatically_uses_the_active_timeline_selection()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "selected-export.mkv")]);
        var media = viewModel.SelectedMedia!;
        media.SelectionStartSeconds = 5;
        media.SelectionEndSeconds = 12;

        var result = await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "selected-export.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(
            new MediaRange(new MediaTime(5, 1), new MediaTime(12, 1)),
            Assert.Single(renderer.Plan!.SourceRanges));
        Assert.Equal(new MediaTime(7, 1), renderer.Plan.ExpectedDuration);
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
        externalTrack.TimelineOffsetSeconds = 2.5;

        var previewTrack = Assert.Single(viewModel.PreviewAudioTracks, track => track.IsExternal);
        Assert.Equal(musicPath, previewTrack.ExternalSourcePath);
        Assert.Equal(-8, previewTrack.GainDb);
        Assert.Equal(new MediaTime(5, 2), previewTrack.TimelineOffset);
        Assert.True(viewModel.CanExport);

        await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "mixed.mp4"),
            replaceExistingDestination: false);

        var exportedTrack = Assert.Single(renderer.Plan!.AudioTracks, track => track.IsExternal);
        Assert.Equal(musicPath, exportedTrack.ExternalSourcePath);
        Assert.Equal(-8, exportedTrack.GainDb);
        Assert.Equal(new MediaTime(5, 2), exportedTrack.TimelineOffset);
    }

    [Fact]
    public async Task Independent_audio_cut_leaves_silence_and_does_not_block_export()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "source.mkv")]);
        var audioTrack = Assert.Single(viewModel.AudioTracks);
        audioTrack.SelectionStartSeconds = 2;
        audioTrack.SelectionEndSeconds = 4;

        Assert.True(audioTrack.RemoveSelection());
        Assert.True(viewModel.CanExport);
        var previewTrack = Assert.Single(viewModel.PreviewAudioTracks);
        Assert.Equal(new MediaTime(58, 1), previewTrack.AudioEdit!.OutputDuration);

        await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "audio-cut.mp4"),
            replaceExistingDestination: false);

        var exportedTrack = Assert.Single(renderer.Plan!.AudioTracks);
        Assert.Equal<MediaRange>(audioTrack.KeptRanges, exportedTrack.AudioEdit!.KeptRanges);
        Assert.Equal(new MediaTime(60, 1), exportedTrack.AudioEdit.SourceDuration);
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
            var musicPath = Path.Combine(Path.GetTempPath(), "saved music.flac");
            await original.ImportFilesAsync([sourcePath, musicPath]);
            var media = original.SelectedMedia!;
            media.Crop = new CropRegion(media.VideoSize, 420, 0, 1_080, 1_080);
            media.PlayheadSeconds = 5;
            media.MarkSelectionStart();
            media.PlayheadSeconds = 10;
            media.MarkSelectionEnd();
            media.RemoveSelection();
            var audioTrack = Assert.Single(original.AudioTracks, track => !track.IsExternal);
            audioTrack.SelectionStartSeconds = 12;
            audioTrack.SelectionEndSeconds = 14;
            audioTrack.RemoveSelection();
            audioTrack.GainDb = -4.5;
            audioTrack.IsMuted = true;
            var externalTrack = Assert.Single(original.AudioTracks, track => track.IsExternal);
            externalTrack.TimelineOffsetSeconds = 3.25;
            original.SelectedExportPreset = BuiltInExportPresets.WebM;
            Assert.True(await original.SaveProjectAsync(projectPath));
            Assert.False(original.IsProjectDirty);

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await restored.OpenProjectAsync(projectPath));

            Assert.Equal(BuiltInExportPresets.WebM, restored.SelectedExportPreset);
            Assert.Equal(media.Crop, restored.SelectedMedia!.Crop);
            Assert.Equal(media.Edit!.SourceDuration, restored.SelectedMedia.Edit!.SourceDuration);
            Assert.Equal<MediaRange>(media.KeptRanges, restored.SelectedMedia.KeptRanges);
            var restoredAudio = Assert.Single(restored.AudioTracks, track => !track.IsExternal);
            Assert.Equal(audioTrack.GainDb, restoredAudio.GainDb);
            Assert.Equal(audioTrack.IsMuted, restoredAudio.IsMuted);
            Assert.Equal<MediaRange>(audioTrack.KeptRanges, restoredAudio.KeptRanges);
            var restoredExternal = Assert.Single(restored.AudioTracks, track => track.IsExternal);
            Assert.Equal(new MediaTime(13, 4), restoredExternal.TimelineOffset);
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

    [AvaloniaFact]
    public async Task Timeline_previews_resample_the_current_zoomed_viewport()
    {
        var decoder = new RecordingFrameDecoder();
        using var viewModel = new MainWindowViewModel(new StubProbe(), frameDecoder: decoder);

        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "timeline-preview.mkv")]);
        await decoder.WaitForCallCountAsync(12);
        var media = viewModel.SelectedMedia!;

        Assert.Equal(12, media.TimelineThumbnails.Count);
        Assert.Equal(2.5, decoder.TimelineTimestamps[0], 3);
        Assert.Equal(57.5, decoder.TimelineTimestamps[11], 3);

        media.ZoomTimeline(2, anchorSeconds: 30);
        await decoder.WaitForCallCountAsync(24);

        Assert.Equal(2, media.TimelineZoom);
        Assert.Equal(15, media.TimelineViewportStart, 3);
        Assert.Equal(16.25, decoder.TimelineTimestamps[12], 3);
        Assert.Equal(43.75, decoder.TimelineTimestamps[23], 3);
    }

    [AvaloniaFact]
    public async Task Mixer_waveform_regenerates_for_the_zoomed_visible_range()
    {
        var renderer = new RecordingWaveformRenderer();
        using var viewModel = new MainWindowViewModel(
            new StubProbe(),
            waveformRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "waveform.mkv")]);
        var track = Assert.Single(viewModel.AudioTracks);

        viewModel.ToggleAudioMixer();
        await renderer.WaitForCallCountAsync(1);

        Assert.True(track.HasWaveform);
        Assert.Equal(MediaTime.Zero, renderer.Ranges[0].Start);
        Assert.Equal(new MediaTime(60, 1), renderer.Ranges[0].End);
        Assert.Equal(new PixelSize(1_600, 72), renderer.Sizes[0]);

        track.ZoomTimeline(2, anchorSeconds: 30);
        await renderer.WaitForCallCountAsync(2);

        Assert.Equal(new MediaTime(15, 1), renderer.Ranges[1].Start);
        Assert.Equal(new MediaTime(45, 1), renderer.Ranges[1].End);
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

    private sealed class RecordingFrameDecoder : IFrameDecoder
    {
        private readonly List<double> _timelineTimestamps = [];

        public IReadOnlyList<double> TimelineTimestamps => _timelineTimestamps;

        public Task<DecodedFrame> DecodeAsync(
            string sourcePath,
            int videoStreamIndex,
            MediaTime timestamp,
            PixelSize maximumSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximumSize == new PixelSize(240, 112))
            {
                _timelineTimestamps.Add(timestamp.TotalSeconds);
            }
            return Task.FromResult(new DecodedFrame(TinyPng, "image/png"));
        }

        public async Task WaitForCallCountAsync(int count)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (_timelineTimestamps.Count < count && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10);
            }

            Assert.True(
                _timelineTimestamps.Count >= count,
                $"Expected {count} timeline frame requests, received {_timelineTimestamps.Count}.");
        }
    }

    private sealed class RecordingWaveformRenderer : IWaveformRenderer
    {
        private readonly List<MediaRange> _ranges = [];
        private readonly List<PixelSize> _sizes = [];

        public IReadOnlyList<MediaRange> Ranges => _ranges;

        public IReadOnlyList<PixelSize> Sizes => _sizes;

        public Task<WaveformImage> RenderAsync(
            string sourcePath,
            int audioStreamIndex,
            MediaRange visibleRange,
            PixelSize outputSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ranges.Add(visibleRange);
            _sizes.Add(outputSize);
            return Task.FromResult(new WaveformImage(TinyPng, "image/png"));
        }

        public async Task WaitForCallCountAsync(int count)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (_ranges.Count < count && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10);
            }

            Assert.True(_ranges.Count >= count, $"Expected {count} waveform requests, received {_ranges.Count}.");
        }
    }

    private sealed class RecordingProjectStore : IProjectStore
    {
        public TaskCompletionSource<ProjectDocument> Saved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? SavedPath { get; private set; }

        public string? DeletedPath { get; private set; }

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
            DeletedPath = projectPath;
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
