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
        Assert.False(viewModel.ShowRangeStrip);
        Assert.True(viewModel.ShowTimeline);
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
    public async Task Crop_preset_auto_applies_and_manual_resize_returns_to_custom()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "crop-preset.mkv")]);
        var clip = viewModel.SelectedVideoClip!;
        viewModel.SelectedCropAspectPreset = BuiltInCropAspectPresets.Square;

        Assert.Equal(new CropRegion(clip.VideoSize, 420, 0, 1_080, 1_080), clip.SourceWindow);
        Assert.Same(BuiltInCropAspectPresets.Square, viewModel.SelectedCropAspectPreset);
        Assert.False(viewModel.IsCropAspectLocked);

        clip.SourceWindow = CropRegion.FromEdges(
            clip.VideoSize,
            clip.SourceWindow.X,
            clip.SourceWindow.Y,
            clip.SourceWindow.Right + 120,
            clip.SourceWindow.Bottom);

        Assert.Equal(1_200, clip.SourceWindow.Width);
        Assert.Equal(1_080, clip.SourceWindow.Height);
        Assert.Same(BuiltInCropAspectPresets.Custom, viewModel.SelectedCropAspectPreset);
        Assert.True(viewModel.IsProjectDirty);
    }

    [Fact]
    public async Task Same_crop_preset_applies_to_every_video_but_positions_remain_independent()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync(
        [
            Path.Combine(Path.GetTempPath(), "crop-first.mkv"),
            Path.Combine(Path.GetTempPath(), "crop-second.mkv"),
        ]);
        var clips = viewModel.VideoClips.ToArray();
        viewModel.SelectedCropAspectPreset = BuiltInCropAspectPresets.Portrait916;

        Assert.All(clips, clip => Assert.Equal(new PixelSize(603, 1_072), clip.SourceWindow.ExportSize));

        clips[0].SourceWindow = clips[0].SourceWindow.MoveClamped(0, 0);

        Assert.Equal(0, clips[0].SourceWindow.X);
        Assert.Equal(659, clips[1].SourceWindow.X);
        Assert.Equal(new PixelSize(603, 1_072), clips[1].SourceWindow.ExportSize);

        clips[0].SourceWindow = new CropRegion(clips[0].VideoSize, 50, 100, 500, 800);

        Assert.Same(BuiltInCropAspectPresets.Custom, viewModel.SelectedCropAspectPreset);
        Assert.Equal(new PixelSize(500, 800), clips[1].SourceWindow.ExportSize);
        Assert.NotEqual(clips[0].SourceWindow.X, clips[1].SourceWindow.X);
    }

    [Fact]
    public async Task Video_clips_can_be_selected_and_reordered_without_losing_their_own_crop()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), "sequence-first.mkv");
        var musicPath = Path.Combine(Path.GetTempPath(), "sequence-music.flac");
        var secondPath = Path.Combine(Path.GetTempPath(), "sequence-second.mkv");
        var thirdPath = Path.Combine(Path.GetTempPath(), "sequence-third.mkv");
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([firstPath, musicPath, secondPath, thirdPath]);
        viewModel.SelectedCropAspectPreset = BuiltInCropAspectPresets.Square;
        var clips = viewModel.VideoClips.ToArray();
        clips[1].SourceWindow = clips[1].SourceWindow.MoveClamped(100, 0);
        viewModel.SelectedVideoClip = clips[1];

        Assert.True(viewModel.CanMoveSelectedVideoLeft);
        Assert.True(viewModel.CanMoveSelectedVideoRight);
        Assert.True(viewModel.MoveSelectedVideoLeft());

        Assert.Equal(
            [secondPath, firstPath, thirdPath],
            viewModel.VideoClips.Select(clip => clip.SourcePath));
        Assert.Same(clips[1], viewModel.SelectedVideoClip);
        Assert.Equal(new CropRegion(clips[1].VideoSize, 100, 0, 1_080, 1_080), clips[1].SourceWindow);
        Assert.Equal(
            [secondPath, firstPath, thirdPath],
            viewModel.CreateProjectDocument().VideoClips!
                .Select(document => viewModel.MediaItems.Single(media => media.Id == document.SourceMediaId).SourcePath));

        Assert.True(viewModel.ReorderVideoClip(clips[1], clips[2], insertAfterTarget: true));
        Assert.Equal(
            [firstPath, thirdPath, secondPath],
            viewModel.VideoClips.Select(clip => clip.SourcePath));
    }

    [Fact]
    public async Task Reordered_clips_and_independent_crop_positions_survive_project_round_trip()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), $"crop-sequence-{Guid.NewGuid():N}.clipedit");
        var firstPath = Path.Combine(Path.GetTempPath(), "roundtrip-first.mkv");
        var secondPath = Path.Combine(Path.GetTempPath(), "roundtrip-second.mkv");
        var store = new JsonProjectStore();
        try
        {
            using (var original = new MainWindowViewModel(new StubProbe(), projectStore: store))
            {
                await original.ImportFilesAsync([firstPath, secondPath]);
                original.SelectedCropAspectPreset = BuiltInCropAspectPresets.Square;
                var clips = original.VideoClips.ToArray();
                clips[0].SourceWindow = clips[0].SourceWindow.MoveClamped(0, 0);
                clips[1].SourceWindow = clips[1].SourceWindow.MoveClamped(800, 0);
                original.SelectedVideoClip = clips[1];
                original.IsCropAspectLocked = true;
                Assert.True(original.MoveSelectedVideoLeft());
                Assert.True(await original.SaveProjectAsync(projectPath));
            }

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await restored.OpenProjectAsync(projectPath));
            var restoredClips = restored.VideoClips.ToArray();
            Assert.Equal([secondPath, firstPath], restoredClips.Select(clip => clip.SourcePath));
            Assert.Equal(800, restoredClips[0].SourceWindow.X);
            Assert.Equal(0, restoredClips[1].SourceWindow.X);
            Assert.All(restoredClips, clip => Assert.Equal(new PixelSize(1_080, 1_080), clip.SourceWindow.ExportSize));
            Assert.Same(BuiltInCropAspectPresets.Square, restored.SelectedCropAspectPreset);
            Assert.True(restored.IsCropAspectLocked);
        }
        finally
        {
            File.Delete(projectPath);
        }
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
    public async Task Removing_a_sequence_range_splits_the_clip_and_ripples_the_gap_closed()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "sequence-remove.mkv")]);
        viewModel.SequenceSelectionStartSeconds = 10;
        viewModel.SequenceSelectionEndSeconds = 20;

        Assert.True(viewModel.RemoveSequenceSelection());

        Assert.Equal(2, viewModel.VideoClips.Count);
        Assert.Equal(
            [
                new MediaRange(MediaTime.Zero, new MediaTime(10, 1)),
                new MediaRange(new MediaTime(20, 1), new MediaTime(60, 1)),
            ],
            viewModel.VideoClips.Select(clip => clip.Model.SourceRange));
        Assert.Equal(50, viewModel.SequenceDurationSeconds);
        Assert.Equal(viewModel.SequenceSelectionStartSeconds, viewModel.SequenceSelectionEndSeconds);
        Assert.All(viewModel.VideoClips, clip =>
            Assert.Equal(new MediaRange(MediaTime.Zero, new MediaTime(60, 1)), clip.Model.AvailableRange));
    }

    [Fact]
    public async Task Keep_only_hides_outer_sections_and_trim_edges_can_restore_them()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "sequence-keep.mkv")]);
        viewModel.SequenceSelectionStartSeconds = 5;
        viewModel.SequenceSelectionEndSeconds = 12;

        Assert.True(viewModel.KeepSequenceSelectionOnly());

        var clip = Assert.Single(viewModel.VideoClips);
        Assert.Equal(new MediaRange(new MediaTime(5, 1), new MediaTime(12, 1)), clip.Model.SourceRange);
        Assert.True(clip.HasHeadHandle);
        Assert.True(clip.HasTailHandle);

        clip.SourceStartSeconds = 3;

        Assert.Equal(3.003, clip.SourceStart.TotalSeconds, precision: 3);
        Assert.Equal(new MediaTime(12, 1), clip.SourceEnd);
        Assert.Equal(8.997, viewModel.SequenceDurationSeconds, precision: 3);
    }

    [Fact]
    public async Task Split_and_delete_operate_on_the_selected_timeline_clip_not_the_source_asset()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "sequence-split.mkv")]);
        viewModel.SequencePlayheadSeconds = 30;

        Assert.True(viewModel.SplitSelectedVideoClip());
        Assert.Equal(2, viewModel.VideoClips.Count);
        Assert.Single(viewModel.MediaItems);
        Assert.Equal(new MediaTime(30, 1), viewModel.VideoClips[0].SourceEnd);
        Assert.Equal(new MediaTime(30, 1), viewModel.VideoClips[1].SourceStart);

        Assert.True(viewModel.DeleteSelectedVideoClip());

        Assert.Single(viewModel.VideoClips);
        Assert.Single(viewModel.MediaItems);
        Assert.Equal(new MediaRange(MediaTime.Zero, new MediaTime(30, 1)), viewModel.VideoClips[0].Model.SourceRange);
    }

    [Fact]
    public async Task Export_uses_the_selected_preset_and_current_exact_edits()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "source.mkv")]);
        viewModel.SequenceSelectionStartSeconds = 5;
        viewModel.SequenceSelectionEndSeconds = 10;
        Assert.Single(viewModel.AudioTracks).GainDb = -3;
        viewModel.SelectedExportPreset = BuiltInExportPresets.WebM;
        var destination = Path.Combine(Path.GetTempPath(), "rendered clip.webm");

        var result = await viewModel.ExportAsync(destination, replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(BuiltInExportPresets.WebM, renderer.Plan!.Preset);
        var segment = Assert.Single(renderer.Plan!.VideoSegments);
        Assert.Equal(new MediaRange(new MediaTime(5, 1), new MediaTime(10, 1)), segment.SourceRange);
        Assert.Equal(viewModel.SelectedVideoClip!.SourceWindow, segment.Crop);
        Assert.Equal(-3, Assert.Single(segment.AudioTracks).GainDb);
        Assert.Equal(destination, renderer.Plan.DestinationPath);
        Assert.Equal("source-clip.webm", viewModel.GetSuggestedExportFileName());
    }

    [Fact]
    public async Task Export_automatically_uses_the_active_timeline_selection()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "selected-export.mkv")]);
        viewModel.SequenceSelectionStartSeconds = 5;
        viewModel.SequenceSelectionEndSeconds = 12;

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
    public async Task Export_selected_range_can_cross_multiple_sources_in_sequence_order()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        var firstPath = Path.Combine(Path.GetTempPath(), "export-first.mkv");
        var secondPath = Path.Combine(Path.GetTempPath(), "export-second.mkv");
        await viewModel.ImportFilesAsync([firstPath, secondPath]);
        viewModel.SequenceSelectionStartSeconds = 55;
        viewModel.SequenceSelectionEndSeconds = 65;

        var result = await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "multi-export.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(2, renderer.Plan!.VideoSegments.Length);
        Assert.Equal(firstPath, renderer.Plan.VideoSegments[0].SourcePath);
        Assert.Equal(new MediaRange(new MediaTime(55, 1), new MediaTime(60, 1)), renderer.Plan.VideoSegments[0].SourceRange);
        Assert.Equal(secondPath, renderer.Plan.VideoSegments[1].SourcePath);
        Assert.Equal(new MediaRange(MediaTime.Zero, new MediaTime(5, 1)), renderer.Plan.VideoSegments[1].SourceRange);
        Assert.Equal(new MediaTime(10, 1), renderer.Plan.ExpectedDuration);
        Assert.Equal(new MediaTime(55, 1), renderer.Plan.SequenceTimelineStart);
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

        var exportedTrack = Assert.Single(Assert.Single(renderer.Plan!.VideoSegments).AudioTracks);
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

        viewModel.SelectedVideoClip!.CropWidth = 1_919;

        Assert.False(viewModel.CanExport);
        Assert.Contains("even", viewModel.ExportAvailabilityText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new PixelSize(1_919, 1_080), viewModel.SelectedVideoClip.SourceWindow.ExportSize);
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
    public async Task Sequence_filmstrip_resamples_the_current_zoomed_viewport()
    {
        var decoder = new RecordingFrameDecoder();
        using var viewModel = new MainWindowViewModel(new StubProbe(), frameDecoder: decoder);

        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "timeline-preview.mkv")]);
        await decoder.WaitForCallCountAsync(14);
        var clip = viewModel.SelectedVideoClip!;

        Assert.Equal(14, clip.TimelineThumbnails.Count);
        var initialTimestamps = decoder.TimelineTimestamps.Take(14).Order().ToArray();
        Assert.Equal(2, initialTimestamps[0], 3);
        Assert.Equal(58, initialTimestamps[13], 3);
        Assert.True(decoder.MaximumConcurrentTimelineDecodes >= 2);

        viewModel.ZoomSequenceTimeline(2, anchor: 30);
        await decoder.WaitForCallCountAsync(28);

        Assert.Equal(2, viewModel.SequenceTimelineZoom);
        Assert.Equal(15, viewModel.SequenceTimelineViewportStart, 3);
        var zoomedTimestamps = decoder.TimelineTimestamps.Skip(14).Take(14).Order().ToArray();
        Assert.Equal(17, zoomedTimestamps[0], 3);
        Assert.Equal(43, zoomedTimestamps[13], 3);
    }

    [AvaloniaFact]
    public async Task Timeline_hover_uses_a_warm_filmstrip_frame_before_exact_refinement()
    {
        var decoder = new RecordingFrameDecoder();
        using var viewModel = new MainWindowViewModel(new StubProbe(), frameDecoder: decoder);

        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "hover-preview.mkv")]);
        await decoder.WaitForCallCountAsync(14);

        viewModel.TimelineHoverTime = 5;

        Assert.True(viewModel.HasTimelineHoverPreview);
        Assert.Equal(0, decoder.HoverCalls);
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
        private readonly object _gate = new();
        private readonly List<double> _timelineTimestamps = [];
        private int _activeTimelineDecodes;
        private int _maximumConcurrentTimelineDecodes;
        private int _hoverCalls;

        public IReadOnlyList<double> TimelineTimestamps
        {
            get
            {
                lock (_gate)
                {
                    return _timelineTimestamps.ToArray();
                }
            }
        }

        public int MaximumConcurrentTimelineDecodes => Volatile.Read(ref _maximumConcurrentTimelineDecodes);

        public int HoverCalls => Volatile.Read(ref _hoverCalls);

        public async Task<DecodedFrame> DecodeAsync(
            string sourcePath,
            int videoStreamIndex,
            MediaTime timestamp,
            PixelSize maximumSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximumSize == new PixelSize(240, 120))
            {
                var active = Interlocked.Increment(ref _activeTimelineDecodes);
                UpdateMaximum(active);
                try
                {
                    await Task.Delay(15, cancellationToken);
                    lock (_gate)
                    {
                        _timelineTimestamps.Add(timestamp.TotalSeconds);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeTimelineDecodes);
                }
            }
            else if (maximumSize == new PixelSize(360, 202))
            {
                Interlocked.Increment(ref _hoverCalls);
            }

            return new DecodedFrame(TinyPng, "image/png");
        }

        public async Task WaitForCallCountAsync(int count)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (TimelineTimestamps.Count < count && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10);
            }

            Assert.True(
                TimelineTimestamps.Count >= count,
                $"Expected {count} timeline frame requests, received {TimelineTimestamps.Count}.");
            await Task.Delay(25);
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentTimelineDecodes);
                if (active <= current ||
                    Interlocked.CompareExchange(ref _maximumConcurrentTimelineDecodes, active, current) == current)
                {
                    return;
                }
            }
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
