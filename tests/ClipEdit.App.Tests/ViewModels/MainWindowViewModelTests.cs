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
        Assert.False(viewModel.ShowAudioMixer);
        viewModel.IsAdvancedMode = true;
        Assert.True(viewModel.ShowAudioMixer);
        Assert.Equal(2, viewModel.VideoItems.Count());
        Assert.Single(viewModel.ExternalAudioItems);
    }

    [Fact]
    public async Task Embedded_audio_from_later_videos_joins_existing_logical_lanes()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        var firstPath = Path.Combine(Path.GetTempPath(), "lane-first.mkv");
        var secondPath = Path.Combine(Path.GetTempPath(), "lane-second.mkv");

        await viewModel.ImportFilesAsync([firstPath, secondPath]);

        var lane = Assert.Single(viewModel.AudioTracks, track => !track.IsExternal);
        Assert.Equal(0, lane.EmbeddedLaneIndex);
        Assert.True(lane.HasEmbeddedSource(firstPath, 1));
        Assert.True(lane.HasEmbeddedSource(secondPath, 1));
        Assert.Equal([firstPath, secondPath], lane.TimelineSegments.Select(segment => segment.SourcePath));

        viewModel.SelectedVideoClip = viewModel.VideoClips[1];
        Assert.True(viewModel.ToggleSelectedClipAudioMembership(lane));
        Assert.Single(lane.TimelineSegments);
        Assert.False(viewModel.VideoClips[1].IncludesAudioLane(0));

        await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "lane-membership.mp4"),
            replaceExistingDestination: false);

        Assert.Single(renderer.Plan!.VideoSegments[0].AudioTracks);
        Assert.Empty(renderer.Plan.VideoSegments[1].AudioTracks);
        Assert.Equal([0], viewModel.CreateProjectDocument().VideoClips![1].ExcludedAudioLaneIndices);
    }

    [Fact]
    public async Task Removed_audio_lane_stays_removed_after_project_round_trip_and_can_be_restored()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), $"audio-lane-{Guid.NewGuid():N}.clipedit");
        try
        {
            var store = new JsonProjectStore();
            using (var original = new MainWindowViewModel(new StubProbe(), projectStore: store))
            {
                await original.ImportFilesAsync(
                [
                    Path.Combine(Path.GetTempPath(), "remove-lane-first.mkv"),
                    Path.Combine(Path.GetTempPath(), "remove-lane-second.mkv"),
                ]);
                Assert.True(original.RemoveAudioTrack(Assert.Single(original.AudioTracks)));
                Assert.Empty(original.AudioTracks);
                Assert.True(original.CanRestoreMissingAudioTracks);
                Assert.True(await original.SaveProjectAsync(projectPath));
            }

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await restored.OpenProjectAsync(projectPath));
            Assert.Empty(restored.AudioTracks);
            Assert.True(restored.CanRestoreMissingAudioTracks);
            Assert.True(restored.RestoreMissingAudioTracks());
            var restoredLane = Assert.Single(restored.AudioTracks);
            Assert.Equal(2, restoredLane.EmbeddedSourcePaths.Count);
        }
        finally
        {
            File.Delete(projectPath);
        }
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
        viewModel.ExportScalePercent = 40;
        viewModel.ExportPlaybackSpeedPercent = 200;
        viewModel.SelectedExportQuality = ExportQualityChoice.Custom;
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
        Assert.Equal(100, viewModel.ExportScalePercent);
        Assert.Equal(100, viewModel.ExportPlaybackSpeedPercent);
        Assert.Equal(ExportQualityMode.MatchSource, viewModel.ExportQualityMode);
        Assert.NotEqual(previousProjectId, viewModel.CreateProjectDocument().ProjectId);
        Assert.EndsWith($"{previousProjectId:N}.recovery.clipedit", store.DeletedPath);
    }

    [Fact]
    public async Task Dirty_project_can_start_open_action_but_still_requires_explicit_discard()
    {
        var store = new RecordingProjectStore();
        using var viewModel = new MainWindowViewModel(
            new StubProbe(),
            projectStore: store,
            recoveryDirectory: Path.GetTempPath(),
            autosaveDelay: TimeSpan.FromHours(1));
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "current-project.mkv")]);

        Assert.True(viewModel.IsProjectDirty);
        Assert.True(viewModel.CanOpenProject);
        Assert.False(await viewModel.OpenProjectWithRelinkingAsync("replacement.clipedit"));
        Assert.Single(viewModel.MediaItems);
        Assert.Contains("save", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
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
        viewModel.SelectedCropAspectPreset = BuiltInCropAspectPresets.Square;

        Assert.Equal(new CropRegion(viewModel.CanvasSize, 420, 0, 1_080, 1_080), viewModel.CanvasCrop);
        Assert.Same(BuiltInCropAspectPresets.Square, viewModel.SelectedCropAspectPreset);
        Assert.False(viewModel.IsCropAspectLocked);

        viewModel.CanvasCrop = CropRegion.FromEdges(
            viewModel.CanvasSize,
            viewModel.CanvasCrop.X,
            viewModel.CanvasCrop.Y,
            viewModel.CanvasCrop.Right + 120,
            viewModel.CanvasCrop.Bottom);

        Assert.Equal(1_200, viewModel.CanvasCrop.Width);
        Assert.Equal(1_080, viewModel.CanvasCrop.Height);
        Assert.Same(BuiltInCropAspectPresets.Custom, viewModel.SelectedCropAspectPreset);
        Assert.True(viewModel.IsProjectDirty);
    }

    [Fact]
    public async Task One_crop_frame_stays_shared_while_clip_transforms_remain_independent()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync(
        [
            Path.Combine(Path.GetTempPath(), "crop-first.mkv"),
            Path.Combine(Path.GetTempPath(), "crop-second.mkv"),
        ]);
        var clips = viewModel.VideoClips.ToArray();
        viewModel.SelectedCropAspectPreset = BuiltInCropAspectPresets.Portrait916;

        Assert.Equal(new PixelSize(594, 1_056), viewModel.CanvasCrop.ExportSize);
        Assert.Equal(0, viewModel.CanvasCrop.Width % 2);
        Assert.Equal(9 * viewModel.CanvasCrop.Height, 16 * viewModel.CanvasCrop.Width);
        Assert.All(clips, clip => Assert.Equal(CropRegion.FullFrame(clip.VideoSize), clip.SourceWindow));

        clips[0].CanvasOffsetX = 240;

        Assert.Equal(240, clips[0].CanvasTransform.OffsetX);
        Assert.Equal(0, clips[1].CanvasTransform.OffsetX);
        Assert.Equal(new PixelSize(594, 1_056), viewModel.CanvasCrop.ExportSize);

        viewModel.CanvasCrop = new CropRegion(viewModel.CanvasSize, 50, 100, 500, 800);

        Assert.Same(BuiltInCropAspectPresets.Custom, viewModel.SelectedCropAspectPreset);
        Assert.Equal(new PixelSize(500, 800), viewModel.CanvasCrop.ExportSize);
        Assert.NotEqual(clips[0].CanvasTransform.OffsetX, clips[1].CanvasTransform.OffsetX);
    }

    [Fact]
    public async Task Video_clips_can_be_selected_and_reordered_without_losing_their_transform()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), "sequence-first.mkv");
        var musicPath = Path.Combine(Path.GetTempPath(), "sequence-music.flac");
        var secondPath = Path.Combine(Path.GetTempPath(), "sequence-second.mkv");
        var thirdPath = Path.Combine(Path.GetTempPath(), "sequence-third.mkv");
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([firstPath, musicPath, secondPath, thirdPath]);
        viewModel.SelectedCropAspectPreset = BuiltInCropAspectPresets.Square;
        var clips = viewModel.VideoClips.ToArray();
        clips[1].CanvasOffsetX = 100;
        viewModel.SelectedVideoClip = clips[1];

        Assert.True(viewModel.CanMoveSelectedVideoLeft);
        Assert.True(viewModel.CanMoveSelectedVideoRight);
        Assert.True(viewModel.MoveSelectedVideoLeft());

        Assert.Equal(
            [secondPath, firstPath, thirdPath],
            viewModel.VideoClips.Select(clip => clip.SourcePath));
        Assert.Same(clips[1], viewModel.SelectedVideoClip);
        Assert.Equal(100, clips[1].CanvasTransform.OffsetX);
        Assert.Equal(new PixelSize(1_080, 1_080), viewModel.CanvasCrop.ExportSize);
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
    public async Task Reordered_clips_shared_crop_and_independent_transforms_survive_project_round_trip()
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
                clips[0].CanvasOffsetX = -100;
                clips[1].CanvasOffsetX = 800;
                clips[1].CanvasScaleXPercent = 125;
                clips[1].CanvasScaleYPercent = 75;
                clips[1].CanvasRotationDegrees = 15;
                original.SelectedVideoClip = clips[1];
                original.IsCropAspectLocked = true;
                Assert.True(original.MoveSelectedVideoLeft());
                Assert.True(await original.SaveProjectAsync(projectPath));
            }

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await restored.OpenProjectAsync(projectPath));
            var restoredClips = restored.VideoClips.ToArray();
            Assert.Equal([secondPath, firstPath], restoredClips.Select(clip => clip.SourcePath));
            Assert.Equal(800, restoredClips[0].CanvasTransform.OffsetX);
            Assert.Equal(1.25, restoredClips[0].CanvasTransform.ScaleX);
            Assert.Equal(0.75, restoredClips[0].CanvasTransform.ScaleY);
            Assert.Equal(15, restoredClips[0].CanvasTransform.RotationDegrees);
            Assert.Equal(-100, restoredClips[1].CanvasTransform.OffsetX);
            Assert.Equal(new PixelSize(1_080, 1_080), restored.CanvasCrop.ExportSize);
            Assert.Same(BuiltInCropAspectPresets.Square, restored.SelectedCropAspectPreset);
            Assert.True(restored.IsCropAspectLocked);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task Schema_two_clip_windows_migrate_to_one_crop_and_equivalent_clip_transforms()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), $"schema-two-{Guid.NewGuid():N}.clipedit");
        var store = new JsonProjectStore();
        try
        {
            using (var legacy = new MainWindowViewModel(new StubProbe()))
            {
                await legacy.ImportFilesAsync(
                [
                    Path.Combine(Path.GetTempPath(), "schema-two-first.mkv"),
                    Path.Combine(Path.GetTempPath(), "schema-two-second.mkv"),
                ]);
                var clips = legacy.VideoClips.ToArray();
                clips[0].SourceWindow = new CropRegion(clips[0].VideoSize, 420, 0, 1_080, 1_080);
                clips[1].SourceWindow = new CropRegion(clips[1].VideoSize, 0, 0, 1_080, 1_080);
                var schemaTwo = legacy.CreateProjectDocument() with
                {
                    SchemaVersion = 2,
                    Canvas = null,
                };
                await store.SaveAsync(projectPath, schemaTwo);
            }

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await restored.OpenProjectAsync(projectPath));

            Assert.Equal(
                new CropRegion(restored.CanvasSize, 420, 0, 1_080, 1_080),
                restored.CanvasCrop);
            var restoredClips = restored.VideoClips.ToArray();
            Assert.Equal(ClipCanvasTransform.Identity, restoredClips[0].CanvasTransform);
            Assert.Equal(420, restoredClips[1].CanvasTransform.OffsetX);
            Assert.Equal(1, restoredClips[1].CanvasTransform.Scale);
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
    public async Task Removing_a_sequence_range_ripples_unrelated_later_clips_left()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync(
        [
            Path.Combine(Path.GetTempPath(), "sequence-remove-first.mkv"),
            Path.Combine(Path.GetTempPath(), "sequence-remove-later.mkv"),
        ]);
        var laterClip = viewModel.VideoClips[1];
        viewModel.SequenceSelectionStartSeconds = 10;
        viewModel.SequenceSelectionEndSeconds = 20;

        Assert.True(viewModel.RemoveSequenceSelection());

        Assert.Equal(3, viewModel.VideoClips.Count);
        Assert.Equal(new MediaTime(10, 1), viewModel.VideoClips[1].TimelineStart);
        Assert.Equal(laterClip.Id, viewModel.VideoClips[2].Id);
        Assert.Equal(laterClip.Model.SourceRange, viewModel.VideoClips[2].Model.SourceRange);
        Assert.Equal(new MediaTime(50, 1), viewModel.VideoClips[2].TimelineStart);
        Assert.Equal(110, viewModel.SequenceDurationSeconds);
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
        Assert.Equal(MediaTime.Zero, clip.TimelineStart);
        Assert.Same(clip, viewModel.SelectedVideoClip);
        Assert.Equal(7, viewModel.SequenceDurationSeconds);
        Assert.True(clip.HasHeadHandle);
        Assert.True(clip.HasTailHandle);

        clip.SourceStartSeconds = 3;

        Assert.Equal(3.003, clip.SourceStart.TotalSeconds, precision: 3);
        Assert.Equal(new MediaTime(12, 1), clip.SourceEnd);
        Assert.Equal(8.997, viewModel.SequenceDurationSeconds, precision: 3);
    }

    [Fact]
    public async Task Keep_selection_trims_only_intersecting_clips_and_ripples_the_rest()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync(
        [
            Path.Combine(Path.GetTempPath(), "sequence-keep-first.mkv"),
            Path.Combine(Path.GetTempPath(), "sequence-keep-second.mkv"),
        ]);
        var clipsBefore = viewModel.VideoClips.ToArray();
        viewModel.SequenceSelectionStartSeconds = 5;
        viewModel.SequenceSelectionEndSeconds = 12;

        Assert.True(viewModel.KeepSequenceSelectionOnly());

        Assert.Equal(2, viewModel.VideoClips.Count);
        Assert.Equal(
            new MediaRange(new MediaTime(5, 1), new MediaTime(12, 1)),
            viewModel.VideoClips[0].Model.SourceRange);
        Assert.Equal(MediaTime.Zero, viewModel.VideoClips[0].TimelineStart);
        Assert.Equal(clipsBefore[1].Id, viewModel.VideoClips[1].Id);
        Assert.Equal(clipsBefore[1].Model.SourceRange, viewModel.VideoClips[1].Model.SourceRange);
        Assert.Equal(new MediaTime(7, 1), viewModel.VideoClips[1].TimelineStart);
        Assert.Same(viewModel.VideoClips[0], viewModel.SelectedVideoClip);
        Assert.Equal(67, viewModel.SequenceDurationSeconds);
        Assert.Equal(0, viewModel.SequenceSelectionStartSeconds);
        Assert.Equal(0, viewModel.SequenceSelectionEndSeconds);
    }

    [Fact]
    public async Task Keep_selection_closes_both_sides_of_a_middle_clip_and_selects_it()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync(
        [
            Path.Combine(Path.GetTempPath(), "sequence-keep-before.mkv"),
            Path.Combine(Path.GetTempPath(), "sequence-keep-middle.mkv"),
            Path.Combine(Path.GetTempPath(), "sequence-keep-after.mkv"),
        ]);
        var untouchedFirst = viewModel.VideoClips[0].Model;
        var untouchedLast = viewModel.VideoClips[2].Model;
        viewModel.SequenceSelectionStartSeconds = 65;
        viewModel.SequenceSelectionEndSeconds = 72;

        Assert.True(viewModel.KeepSequenceSelectionOnly());

        Assert.Equal(3, viewModel.VideoClips.Count);
        Assert.Equal(untouchedFirst, viewModel.VideoClips[0].Model);
        Assert.Equal(new MediaTime(60, 1), viewModel.VideoClips[1].TimelineStart);
        Assert.Equal(
            new MediaRange(new MediaTime(5, 1), new MediaTime(12, 1)),
            viewModel.VideoClips[1].Model.SourceRange);
        Assert.Equal(untouchedLast.Id, viewModel.VideoClips[2].Id);
        Assert.Equal(untouchedLast.SourceRange, viewModel.VideoClips[2].Model.SourceRange);
        Assert.Equal(new MediaTime(67, 1), viewModel.VideoClips[2].TimelineStart);
        Assert.Same(viewModel.VideoClips[1], viewModel.SelectedVideoClip);
        Assert.Equal(127, viewModel.SequenceDurationSeconds);
        Assert.Equal(60, viewModel.SequenceSelectionStartSeconds);
        Assert.Equal(60, viewModel.SequenceSelectionEndSeconds);
    }

    [Fact]
    public async Task Undo_and_redo_restore_timeline_edits_without_reimporting_media()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "undo-split.mkv")]);
        var originalClipId = Assert.Single(viewModel.VideoClips).Id;
        Assert.False(viewModel.CanUndo);
        viewModel.SequencePlayheadSeconds = 30;

        Assert.True(viewModel.SplitSelectedVideoClip());
        Assert.Equal(2, viewModel.VideoClips.Count);
        Assert.True(viewModel.CanUndo);
        Assert.False(viewModel.CanRedo);

        Assert.True(viewModel.Undo());
        Assert.Single(viewModel.VideoClips);
        Assert.Equal(originalClipId, viewModel.VideoClips[0].Id);
        Assert.True(viewModel.CanRedo);

        Assert.True(viewModel.Redo());
        Assert.Equal(2, viewModel.VideoClips.Count);
        Assert.True(viewModel.CanUndo);
    }

    [Fact]
    public async Task Undo_and_redo_restore_keep_selection_while_preserving_other_clips()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync(
        [
            Path.Combine(Path.GetTempPath(), "undo-keep-first.mkv"),
            Path.Combine(Path.GetTempPath(), "undo-keep-second.mkv"),
        ]);
        var originalModels = viewModel.VideoClips.Select(clip => clip.Model).ToArray();
        viewModel.SequenceSelectionStartSeconds = 5;
        viewModel.SequenceSelectionEndSeconds = 12;
        Assert.True(viewModel.KeepSequenceSelectionOnly());

        Assert.True(viewModel.Undo());
        Assert.Equal(originalModels, viewModel.VideoClips.Select(clip => clip.Model));

        Assert.True(viewModel.Redo());
        Assert.Equal(2, viewModel.VideoClips.Count);
        Assert.Equal(
            new MediaRange(new MediaTime(5, 1), new MediaTime(12, 1)),
            viewModel.VideoClips[0].Model.SourceRange);
        Assert.Equal(originalModels[1].Id, viewModel.VideoClips[1].Id);
        Assert.Equal(originalModels[1].SourceRange, viewModel.VideoClips[1].Model.SourceRange);
        Assert.Equal(new MediaTime(7, 1), viewModel.VideoClips[1].TimelineStart);
    }

    [Fact]
    public async Task A_new_edit_after_undo_clears_redo_history()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "undo-branch.mkv")]);
        viewModel.SequencePlayheadSeconds = 30;
        Assert.True(viewModel.SplitSelectedVideoClip());
        Assert.True(viewModel.Undo());
        Assert.True(viewModel.CanRedo);

        viewModel.SequenceSelectionStartSeconds = 5;
        viewModel.SequenceSelectionEndSeconds = 10;
        Assert.True(viewModel.KeepSequenceSelectionOnly());

        Assert.False(viewModel.CanRedo);
    }

    [Fact]
    public async Task Undo_restores_clip_transforms_and_audio_settings_in_order()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "undo-properties.mkv")]);
        var clip = Assert.Single(viewModel.VideoClips);
        var initialTransform = clip.CanvasTransform;
        clip.CanvasTransform = new ClipCanvasTransform(120, -40, 1.25, 17);
        Assert.Single(viewModel.AudioTracks).GainDb = 6;

        Assert.True(viewModel.Undo());
        Assert.Equal(0, Assert.Single(viewModel.AudioTracks).GainDb);
        Assert.Equal(new ClipCanvasTransform(120, -40, 1.25, 17), Assert.Single(viewModel.VideoClips).CanvasTransform);

        Assert.True(viewModel.Undo());
        Assert.Equal(initialTransform, Assert.Single(viewModel.VideoClips).CanvasTransform);

        Assert.True(viewModel.Redo());
        Assert.Equal(new ClipCanvasTransform(120, -40, 1.25, 17), Assert.Single(viewModel.VideoClips).CanvasTransform);
        Assert.True(viewModel.Redo());
        Assert.Equal(6, Assert.Single(viewModel.AudioTracks).GainDb);
    }

    [Fact]
    public async Task Continuous_clip_transform_edit_records_one_history_state_when_completed()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "continuous-transform.mkv")]);
        var clip = Assert.Single(viewModel.VideoClips);
        var initialTransform = clip.CanvasTransform;

        viewModel.BeginClipTransformEdit();
        Assert.True(viewModel.IsClipTransformEditing);
        clip.CanvasTransform = new ClipCanvasTransform(10, -5, 1, 5);
        clip.CanvasTransform = new ClipCanvasTransform(20, -10, 1, 15);
        clip.CanvasTransform = new ClipCanvasTransform(30, -15, 1, 25);

        Assert.False(viewModel.CanUndo);
        viewModel.EndClipTransformEdit();

        Assert.False(viewModel.IsClipTransformEditing);
        Assert.True(viewModel.CanUndo);
        Assert.True(viewModel.Undo());
        Assert.Equal(initialTransform, Assert.Single(viewModel.VideoClips).CanvasTransform);
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public async Task Clip_mirror_buttons_are_independent_persistent_and_undoable()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "mirror-buttons.mkv")]);
        var clip = Assert.Single(viewModel.VideoClips);

        clip.IsHorizontallyMirrored = true;
        clip.IsVerticallyMirrored = true;

        Assert.True(clip.IsHorizontallyMirrored);
        Assert.True(clip.IsVerticallyMirrored);
        var savedClip = Assert.Single(viewModel.CreateProjectDocument().VideoClips!);
        Assert.True(savedClip.IsHorizontallyMirrored);
        Assert.True(savedClip.IsVerticallyMirrored);

        Assert.True(viewModel.Undo());
        clip = Assert.Single(viewModel.VideoClips);
        Assert.False(clip.IsHorizontallyMirrored);
        Assert.False(clip.IsVerticallyMirrored);
        Assert.True(viewModel.Redo());
        clip = Assert.Single(viewModel.VideoClips);
        Assert.True(clip.IsHorizontallyMirrored);
        Assert.True(clip.IsVerticallyMirrored);
    }

    [Fact]
    public async Task Canvas_quarter_turn_rotates_crop_and_every_clip_as_one_undoable_edit()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "rotate-canvas.mkv")]);
        var clip = Assert.Single(viewModel.VideoClips);
        viewModel.CanvasCrop = new CropRegion(viewModel.CanvasSize, 100, 50, 1_000, 800);
        clip.CanvasTransform = new ClipCanvasTransform(120, -40, 1.25, 0.75, 17);
        var originalSize = viewModel.CanvasSize;
        var originalCrop = viewModel.CanvasCrop;
        var originalTransform = clip.CanvasTransform;

        Assert.True(viewModel.RotateCanvasClockwise());

        Assert.Equal(new PixelSize(1_080, 1_920), viewModel.CanvasSize);
        Assert.Equal((230, 100, 800, 1_000),
            (viewModel.CanvasCrop.X, viewModel.CanvasCrop.Y, viewModel.CanvasCrop.Width, viewModel.CanvasCrop.Height));
        Assert.Equal(new ClipCanvasTransform(40, 120, 0.75, 1.25, 107), clip.CanvasTransform);
        Assert.True(viewModel.Undo());
        Assert.Equal(originalSize, viewModel.CanvasSize);
        Assert.Equal(originalCrop, viewModel.CanvasCrop);
        Assert.Equal(originalTransform, Assert.Single(viewModel.VideoClips).CanvasTransform);
    }

    [Fact]
    public async Task Selected_clip_quarter_turn_keeps_placement_and_scale()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "rotate-clip.mkv")]);
        var clip = Assert.Single(viewModel.VideoClips);
        clip.CanvasTransform = new ClipCanvasTransform(25, -15, 1.2, 0.8, 350);

        Assert.True(viewModel.RotateSelectedClipClockwise());

        Assert.Equal(new ClipCanvasTransform(25, -15, 1.2, 0.8, 80), clip.CanvasTransform);
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
    public async Task Empty_sequence_keeps_the_timeline_available_and_media_can_be_added_repeatedly()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "repeatable.mkv")]);

        Assert.True(viewModel.DeleteSelectedVideoClip());
        Assert.Empty(viewModel.VideoClips);
        Assert.True(viewModel.ShowTimeline);

        Assert.True(viewModel.AddMediaToTimeline(viewModel.SelectedMedia));
        Assert.True(viewModel.AddMediaToTimeline(viewModel.SelectedMedia));

        Assert.Equal(2, viewModel.VideoClips.Count);
        Assert.All(viewModel.VideoClips, clip => Assert.Same(viewModel.SelectedMedia, clip.Source));
        Assert.Equal(120, viewModel.SequenceDurationSeconds);
    }

    [Fact]
    public async Task Timeline_copy_and_paste_creates_an_independent_clip_instance_with_the_same_edits()
    {
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "copy-paste.mkv")]);
        var original = Assert.Single(viewModel.VideoClips);
        original.SourceStartSeconds = 5;
        original.SourceEndSeconds = 12;
        original.CanvasTransform = new ClipCanvasTransform(
            12,
            -8,
            1.25,
            0.8,
            7,
            isHorizontallyMirrored: true,
            isVerticallyMirrored: false);
        original.AudioGainDb = -3.5;
        original.SetAudioLaneGainDb(0, 2.25);

        Assert.True(viewModel.CopySelectedVideoClip());
        Assert.True(viewModel.PasteVideoClip());

        var pasted = Assert.IsType<VideoClipViewModel>(viewModel.SelectedVideoClip);
        Assert.NotEqual(original.Id, pasted.Id);
        Assert.Equal(original.Model.SourceRange, pasted.Model.SourceRange);
        Assert.Equal(original.Model.AvailableRange, pasted.Model.AvailableRange);
        Assert.Equal(original.CanvasTransform, pasted.CanvasTransform);
        Assert.Equal(original.AudioGainDb, pasted.AudioGainDb);
        Assert.Equal(2.25, pasted.GetAudioLaneGainDb(0));
        Assert.Equal(original.TimelineEnd, pasted.TimelineStart);
    }

    [Fact]
    public async Task Clips_can_be_placed_with_gaps_and_export_preserves_empty_timeline_time()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        var firstPath = Path.Combine(Path.GetTempPath(), "gap-first.mkv");
        var secondPath = Path.Combine(Path.GetTempPath(), "gap-second.mkv");
        await viewModel.ImportFilesAsync([firstPath, secondPath]);
        var second = viewModel.VideoClips[1];
        viewModel.SequencePlayheadSeconds = 70;

        Assert.True(viewModel.MoveVideoClipTo(second, 80));
        Assert.Equal(80, second.TimelineStartSeconds);
        Assert.Equal(140, viewModel.SequenceDurationSeconds);
        Assert.True(viewModel.IsSequencePlayheadInGap);

        var result = await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "gap-export.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(new MediaTime(140, 1), renderer.Plan!.ExpectedDuration);
        Assert.Equal(MediaTime.Zero, renderer.Plan.GetVideoSegmentTimelineStart(0));
        Assert.Equal(new MediaTime(80, 1), renderer.Plan.GetVideoSegmentTimelineStart(1));

        viewModel.SequencePlayheadSeconds = 70;
        Assert.True(viewModel.IsSequencePlayheadInGap);
        viewModel.SequencePlayheadSeconds = 80;
        Assert.False(viewModel.IsSequencePlayheadInGap);
    }

    [Fact]
    public async Task Clip_speed_ripples_later_clips_and_global_export_speed_scales_the_plan()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync(
        [
            Path.Combine(Path.GetTempPath(), "speed-first.mkv"),
            Path.Combine(Path.GetTempPath(), "speed-second.mkv"),
        ]);
        var first = viewModel.VideoClips[0];
        var second = viewModel.VideoClips[1];
        viewModel.SelectedVideoClip = first;

        Assert.True(viewModel.SetSelectedClipPlaybackSpeed(200));

        Assert.Equal(30, first.DurationSeconds);
        Assert.Equal(30, second.TimelineStartSeconds);
        Assert.Equal(90, viewModel.SequenceDurationSeconds);
        Assert.Equal(200, viewModel.SelectedClipPlaybackSpeedPercent);

        viewModel.SequenceSelectionStartSeconds = 0;
        viewModel.SequenceSelectionEndSeconds = 90;
        viewModel.ExportPlaybackSpeedPercent = 50;
        var result = await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "speed-output.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(200, renderer.Plan!.VideoSegments[0].PlaybackSpeedPercent);
        Assert.Equal(100, renderer.Plan.VideoSegments[1].PlaybackSpeedPercent);
        Assert.Equal(50, renderer.Plan.EncodingSettings.PlaybackSpeedPercent);
        Assert.Equal(new MediaTime(90, 1), renderer.Plan.TimelineDuration);
        Assert.Equal(new MediaTime(180, 1), renderer.Plan.ExpectedDuration);
        var document = viewModel.CreateProjectDocument();
        Assert.Equal(200, document.VideoClips![0].PlaybackSpeedPercent);
        Assert.Equal(100, document.ExportSettings!.PlaybackSpeedPercent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(10000)]
    public void Export_speed_slider_uses_a_logarithmic_round_trip(int playbackSpeedPercent)
    {
        var sliderValue = MainWindowViewModel.PlaybackSpeedPercentToSliderValue(playbackSpeedPercent);

        Assert.Equal(
            playbackSpeedPercent,
            MainWindowViewModel.SliderValueToPlaybackSpeedPercent(sliderValue));
    }

    [Fact]
    public async Task Dropping_a_clip_on_an_occupied_sequence_position_inserts_it_there()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), "insert-first.mkv");
        var secondPath = Path.Combine(Path.GetTempPath(), "insert-second.mkv");
        var thirdPath = Path.Combine(Path.GetTempPath(), "insert-third.mkv");
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([firstPath, secondPath, thirdPath]);
        var third = viewModel.VideoClips[2];

        Assert.True(viewModel.MoveVideoClipTo(third, viewModel.VideoClips[1].TimelineStartSeconds));

        Assert.Equal(
            [firstPath, thirdPath, secondPath],
            viewModel.VideoClips.Select(clip => clip.SourcePath));
        Assert.Equal([0, 60, 120], viewModel.VideoClips.Select(clip => clip.TimelineStartSeconds));
    }

    [Fact]
    public async Task Live_source_playback_updates_sequence_time_and_advances_to_the_next_clip()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), "playback-first.mkv");
        var secondPath = Path.Combine(Path.GetTempPath(), "playback-second.mkv");
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([firstPath, secondPath]);
        viewModel.SequencePlayheadSeconds = 25;

        viewModel.SelectedMedia!.Playhead = new MediaTime(30, 1);

        Assert.Equal(30, viewModel.SequencePlayheadSeconds);
        Assert.True(viewModel.TryAdvanceSequencePlayback());
        Assert.Equal(secondPath, viewModel.SelectedVideoClip!.SourcePath);
        Assert.Equal(60, viewModel.SequencePlayheadSeconds);
        Assert.Equal(MediaTime.Zero, viewModel.SelectedMedia.Playhead);
    }

    [Fact]
    public async Task Preparing_playback_skips_a_gap_and_restarts_the_sequence_only_at_its_end()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), "prepare-first.mkv");
        var secondPath = Path.Combine(Path.GetTempPath(), "prepare-second.mkv");
        var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([firstPath, secondPath]);
        Assert.True(viewModel.MoveVideoClipTo(viewModel.VideoClips[1], 80));
        viewModel.SequencePlayheadSeconds = 70;

        Assert.Equal(MediaTime.Zero, viewModel.PrepareSequencePlayback());
        Assert.Equal(80, viewModel.SequencePlayheadSeconds);
        Assert.Equal(secondPath, viewModel.SelectedVideoClip!.SourcePath);

        viewModel.SequencePlayheadSeconds = 140;
        Assert.Equal(MediaTime.Zero, viewModel.PrepareSequencePlayback());
        Assert.Equal(0, viewModel.SequencePlayheadSeconds);
        Assert.Equal(firstPath, viewModel.SelectedVideoClip!.SourcePath);
    }

    [Fact]
    public async Task Clip_timeline_placement_survives_project_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timeline-placement-{Guid.NewGuid():N}.clipedit");
        var store = new JsonProjectStore();
        try
        {
            using (var original = new MainWindowViewModel(new StubProbe(), projectStore: store))
            {
                await original.ImportFilesAsync([
                    Path.Combine(Path.GetTempPath(), "placement-first.mkv"),
                    Path.Combine(Path.GetTempPath(), "placement-second.mkv")]);
                Assert.True(original.MoveVideoClipTo(original.VideoClips[1], 75));
                Assert.True(await original.SaveProjectAsync(path));
            }

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await restored.OpenProjectAsync(path));
            Assert.Equal(75, restored.VideoClips[1].TimelineStartSeconds);
        }
        finally
        {
            File.Delete(path);
        }
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
        viewModel.SelectedExportQuality = ExportQualityChoice.Custom;
        viewModel.ExportScalePercent = 47;
        viewModel.ExportQuality = 62;
        viewModel.SelectedVideoClip!.AudioGainDb = -2;
        var destination = Path.Combine(Path.GetTempPath(), "rendered clip.webm");

        var result = await viewModel.ExportAsync(destination, replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(BuiltInExportPresets.WebM, renderer.Plan!.Preset);
        Assert.Equal(
            new ExportEncodingSettings(62, 47, 15, qualityMode: ExportQualityMode.Custom),
            renderer.Plan.EncodingSettings);
        Assert.Equal(new PixelSize(902, 506), renderer.Plan.OutputSize);
        var segment = Assert.Single(renderer.Plan!.VideoSegments);
        Assert.Equal(new MediaRange(new MediaTime(5, 1), new MediaTime(10, 1)), segment.SourceRange);
        Assert.Equal(new PixelSize(1_920, 1_080), segment.SourceSize);
        Assert.Equal(viewModel.CanvasSize, segment.CanvasSize);
        Assert.Equal(viewModel.CanvasCrop, segment.CanvasCrop);
        Assert.Equal(viewModel.SelectedVideoClip!.CanvasTransform, segment.CanvasTransform);
        Assert.Equal(-5, Assert.Single(segment.AudioTracks).GainDb);
        Assert.Equal(destination, renderer.Plan.DestinationPath);
        Assert.Equal("source-clip.webm", viewModel.GetSuggestedExportFileName());
    }

    [Fact]
    public async Task Custom_export_builds_the_selected_compatible_format()
    {
        var renderer = new RecordingExportRenderer();
        using var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "custom-export.mkv")]);
        viewModel.SelectedExportPreset = BuiltInExportPresets.Custom;
        viewModel.CustomExportContainer = ExportContainerChoice.Matroska;
        viewModel.CustomVideoCodec = VideoCodecChoice.Vp9;
        viewModel.CustomAudioCodec = AudioCodecChoice.Opus;
        viewModel.CustomUseSourceFrameRate = false;
        viewModel.CustomFrameRate = 48;

        var result = await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "custom-output.mkv"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(ExportContainer.Matroska, renderer.Plan!.Preset.Container);
        Assert.Equal(VideoCodecFamily.Vp9, renderer.Plan.Preset.VideoCodec);
        Assert.Equal(AudioCodecFamily.Opus, renderer.Plan.Preset.AudioCodec);
        Assert.Equal(new FrameRate(48, 1), renderer.Plan.Preset.FrameRate);
        Assert.Equal("custom-export-clip.mkv", viewModel.GetSuggestedExportFileName());
    }

    [Fact]
    public void Named_custom_export_presets_can_be_saved_loaded_and_deleted()
    {
        using var viewModel = new MainWindowViewModel(new StubProbe());
        viewModel.SelectedExportPreset = BuiltInExportPresets.Custom;
        viewModel.CustomExportContainer = ExportContainerChoice.Matroska;
        viewModel.CustomVideoCodec = VideoCodecChoice.Vp9;
        viewModel.CustomAudioCodec = AudioCodecChoice.None;
        viewModel.CustomUseSourceFrameRate = false;
        viewModel.CustomFrameRate = 60;
        viewModel.ExportScalePercent = 55;
        viewModel.ExportQuality = 64;
        viewModel.ExportPlaybackSpeedPercent = 150;
        viewModel.SelectedExportQuality = ExportQualityChoice.Custom;
        viewModel.CustomPresetName = "Small silent VP9";

        Assert.True(viewModel.SaveCustomExportPreset());
        var saved = Assert.Single(viewModel.SavedExportPresets);
        Assert.Equal("Small silent VP9", saved.Name);

        viewModel.CustomExportContainer = ExportContainerChoice.Mp4;
        viewModel.ExportScalePercent = 100;
        viewModel.ExportPlaybackSpeedPercent = 100;
        Assert.True(viewModel.LoadSelectedCustomExportPreset());

        Assert.Same(ExportContainerChoice.Matroska, viewModel.CustomExportContainer);
        Assert.Same(VideoCodecChoice.Vp9, viewModel.CustomVideoCodec);
        Assert.Same(AudioCodecChoice.None, viewModel.CustomAudioCodec);
        Assert.Equal(60, viewModel.CustomFrameRate);
        Assert.Equal(55, viewModel.ExportScalePercent);
        Assert.Equal(64, viewModel.ExportQuality);
        Assert.Equal(150, viewModel.ExportPlaybackSpeedPercent);
        Assert.Equal(ExportQualityMode.Custom, viewModel.ExportQualityMode);
        Assert.True(viewModel.DeleteSelectedCustomExportPreset());
        Assert.Empty(viewModel.SavedExportPresets);
    }

    [Fact]
    public async Task Match_input_resolves_from_the_first_exported_source_and_explains_fallbacks()
    {
        var renderer = new RecordingExportRenderer();
        var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "matched-source.mkv")]);

        viewModel.SelectedExportPreset = BuiltInExportPresets.MatchInput;

        var effective = viewModel.GetEffectiveExportPreset();
        Assert.Equal(ExportParameterMode.Fixed, effective.ParameterMode);
        Assert.Equal(ExportContainer.Matroska, effective.Container);
        Assert.Equal(VideoCodecFamily.H264, effective.VideoCodec);
        Assert.Equal(AudioCodecFamily.Aac, effective.AudioCodec);
        Assert.Equal(new FrameRate(24_000, 1_001), effective.FrameRate);
        Assert.Equal("matched-source-clip.mkv", viewModel.GetSuggestedExportFileName());
        Assert.Contains("fallback", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        var result = await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "matched-output.mkv"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(effective, renderer.Plan!.Preset);
    }

    [Fact]
    public async Task Default_quality_matches_the_first_source_bitrate_until_custom_is_selected()
    {
        using var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "quality-source.mkv")]);

        var matched = viewModel.GetEffectiveExportPreset();

        Assert.Equal(ExportQualityMode.MatchSource, viewModel.ExportQualityMode);
        Assert.Equal(8_000, matched.VideoBitRateBitsPerSecond);
        Assert.Contains("match input quality", viewModel.ExportPlanSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedExportQuality = ExportQualityChoice.Custom;
        var custom = viewModel.GetEffectiveExportPreset();

        Assert.Null(custom.VideoBitRateBitsPerSecond);
        Assert.Contains("quality 75%", viewModel.ExportPlanSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Complete_compatible_source_uses_packet_copy_and_edits_fall_back_to_transcode()
    {
        var renderer = new RecordingExportRenderer();
        using var viewModel = new MainWindowViewModel(
            new CompatibleCopyProbe(),
            exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "copy-source.mp4")]);

        Assert.Contains("no re-encode", viewModel.ExportPlanSummary, StringComparison.OrdinalIgnoreCase);
        var result = await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "copy-output.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(ExportStrategy.StreamCopy, renderer.Plan!.Strategy);

        viewModel.SelectedExportQuality = ExportQualityChoice.Custom;
        Assert.Contains("exact sequence re-encode", viewModel.ExportPlanSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedExportQuality = ExportQualityChoice.MatchSource;
        viewModel.SelectedVideoClip!.CanvasOffsetX = 1;

        Assert.Contains("exact sequence re-encode", viewModel.ExportPlanSummary, StringComparison.OrdinalIgnoreCase);
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
        viewModel.SelectedVideoClip!.AudioGainDb = 2.5;
        Assert.Equal(-5, Assert.Single(viewModel.PreviewAudioTracks).GainDb);
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

        viewModel.CanvasCrop = new CropRegion(viewModel.CanvasSize, 0, 0, 1_919, 1_080);

        Assert.False(viewModel.CanExport);
        Assert.Contains("even", viewModel.ExportAvailabilityText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new PixelSize(1_919, 1_080), viewModel.CanvasCrop.ExportSize);
        Assert.True(viewModel.CanFixExportCompatibility);
        Assert.Equal("Use 1918 × 1080", viewModel.ExportCompatibilityActionText);

        Assert.True(viewModel.MakeExportCropCompatible());
        Assert.True(viewModel.CanExport);
        Assert.Equal(new PixelSize(1_918, 1_080), viewModel.CanvasCrop.ExportSize);
    }

    [Fact]
    public async Task Numeric_crop_resize_snaps_to_export_compatible_dimensions()
    {
        var viewModel = new MainWindowViewModel(
            new StubProbe(),
            exportRenderer: new RecordingExportRenderer());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "source.mkv")]);

        viewModel.CanvasCropWidth = 1_919;
        viewModel.CanvasCropHeight = 1_079;

        Assert.Equal(new PixelSize(1_918, 1_078), viewModel.CanvasCrop.ExportSize);
        Assert.True(viewModel.CanExport);
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
            original.SelectedVideoClip!.AudioGainDb = -6.25;
            original.SelectedVideoClip.SetAudioLaneGainDb(0, -2.75);
            original.SelectedExportPreset = BuiltInExportPresets.Custom;
            original.CustomExportContainer = ExportContainerChoice.Matroska;
            original.CustomVideoCodec = VideoCodecChoice.Vp9;
            original.CustomAudioCodec = AudioCodecChoice.Opus;
            original.CustomUseSourceFrameRate = false;
            original.CustomFrameRate = 48;
            original.ExportScalePercent = 43;
            original.ExportQuality = 58;
            original.GifFrameRate = 21;
            Assert.True(await original.SaveProjectAsync(projectPath));
            Assert.False(original.IsProjectDirty);

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.True(await restored.OpenProjectAsync(projectPath));

            Assert.Equal(BuiltInExportPresets.Custom, restored.SelectedExportPreset);
            Assert.Same(ExportContainerChoice.Matroska, restored.CustomExportContainer);
            Assert.Same(VideoCodecChoice.Vp9, restored.CustomVideoCodec);
            Assert.Same(AudioCodecChoice.Opus, restored.CustomAudioCodec);
            Assert.False(restored.CustomUseSourceFrameRate);
            Assert.Equal(48, restored.CustomFrameRate);
            Assert.Equal(100, restored.ExportScalePercent);
            Assert.Equal(75, restored.ExportQuality);
            Assert.Equal(15, restored.GifFrameRate);
            Assert.Equal(ExportQualityMode.MatchSource, restored.ExportQualityMode);
            Assert.Equal(media.Crop, restored.SelectedMedia!.Crop);
            Assert.Equal(media.Edit!.SourceDuration, restored.SelectedMedia.Edit!.SourceDuration);
            Assert.Equal<MediaRange>(media.KeptRanges, restored.SelectedMedia.KeptRanges);
            var restoredAudio = Assert.Single(restored.AudioTracks, track => !track.IsExternal);
            Assert.Equal(audioTrack.GainDb, restoredAudio.GainDb);
            Assert.Equal(audioTrack.IsMuted, restoredAudio.IsMuted);
            Assert.Equal<MediaRange>(audioTrack.KeptRanges, restoredAudio.KeptRanges);
            var restoredExternal = Assert.Single(restored.AudioTracks, track => track.IsExternal);
            Assert.Equal(new MediaTime(13, 4), restoredExternal.TimelineOffset);
            Assert.Equal(-6.25, restored.SelectedVideoClip!.AudioGainDb);
            Assert.Equal(-2.75, restored.SelectedVideoClip.GetAudioLaneGainDb(0));
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
    public async Task Recovery_discovery_is_explicit_and_identifies_referenced_media()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-recovery-");
        var sourcePath = Path.Combine(directory.FullName, "referenced-video.mkv");
        var recoveryPath = Path.Combine(directory.FullName, $"{Guid.NewGuid():N}.recovery.clipedit");
        var store = new JsonProjectStore();

        try
        {
            using (var source = new MainWindowViewModel(new StubProbe(), projectStore: store))
            {
                await source.ImportFilesAsync([sourcePath]);
                await store.SaveAsync(recoveryPath, source.CreateProjectDocument());
            }

            using var viewModel = new MainWindowViewModel(
                new StubProbe(),
                projectStore: store,
                recoveryDirectory: directory.FullName);

            Assert.Empty(viewModel.RecoveryCandidates);
            Assert.Empty(viewModel.MediaItems);

            await viewModel.DiscoverRecoveryCandidatesAsync();

            var candidate = Assert.Single(viewModel.RecoveryCandidates);
            Assert.True(candidate.CanRecover);
            Assert.Contains("referenced-video.mkv", candidate.ReferencedMedia);
            Assert.Empty(viewModel.MediaItems);
            Assert.True(viewModel.ShowEmptyState);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Unreadable_recovery_can_be_discarded_without_touching_media()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-recovery-corrupt-");
        var recoveryPath = Path.Combine(directory.FullName, $"{Guid.NewGuid():N}.recovery.clipedit");
        var sourcePath = Path.Combine(directory.FullName, "source.mkv");
        await File.WriteAllTextAsync(recoveryPath, "{ not valid json");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);

        try
        {
            using var viewModel = new MainWindowViewModel(
                new StubProbe(),
                projectStore: new JsonProjectStore(),
                recoveryDirectory: directory.FullName);

            await viewModel.DiscoverRecoveryCandidatesAsync();
            var candidate = Assert.Single(viewModel.RecoveryCandidates);
            Assert.False(candidate.CanRecover);
            Assert.NotNull(candidate.ErrorText);

            Assert.True(await viewModel.DiscardRecoveryAsync(candidate));
            Assert.False(File.Exists(recoveryPath));
            Assert.True(File.Exists(sourcePath));
            Assert.Empty(viewModel.RecoveryCandidates);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Missing_source_waits_for_matching_relink_before_restoring_the_project()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-relink-");
        var originalPath = Path.Combine(directory.FullName, "moved-video.mkv");
        var replacementDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "media"));
        var replacementPath = Path.Combine(replacementDirectory.FullName, "moved-video.mkv");
        var projectPath = Path.Combine(directory.FullName, "project.clipedit");
        var store = new JsonProjectStore();
        var expectedTransform = new ClipCanvasTransform(
            123.5,
            -47.25,
            1.4,
            0.75,
            73,
            isHorizontallyMirrored: true);

        try
        {
            await File.WriteAllBytesAsync(originalPath, new byte[1_024]);
            Guid expectedClipId;
            using (var source = new MainWindowViewModel(new StubProbe(), projectStore: store))
            {
                await source.ImportFilesAsync([originalPath]);
                var clip = Assert.Single(source.VideoClips);
                expectedClipId = clip.Id;
                clip.CanvasTransform = expectedTransform;
                Assert.True(await source.SaveProjectAsync(projectPath));
            }

            File.Move(originalPath, replacementPath);

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.False(await restored.OpenProjectWithRelinkingAsync(projectPath));
            Assert.True(restored.HasPendingMissingMedia);
            Assert.Empty(restored.MediaItems);
            var missing = Assert.Single(restored.MissingMediaReferences);
            Assert.Equal("moved-video.mkv", missing.DisplayName);
            Assert.True(missing.HasSuggestion);
            Assert.Equal(Path.GetFullPath(replacementPath), missing.SuggestedPath);

            Assert.True(await restored.RelinkMissingMediaAsync(missing, missing.SuggestedPath!));

            Assert.False(restored.HasPendingMissingMedia);
            Assert.Equal(Path.GetFullPath(replacementPath), Assert.Single(restored.MediaItems).SourcePath);
            var restoredClip = Assert.Single(restored.VideoClips);
            Assert.Equal(expectedClipId, restoredClip.Id);
            Assert.Equal(expectedTransform, restoredClip.CanvasTransform);
            Assert.Same(restoredClip, restored.SelectedVideoClip);
            Assert.Same(restoredClip.Source, restored.SelectedMedia);
            Assert.True(restoredClip.Source.IsReady);
            restored.SequencePlayheadSeconds = 1;
            Assert.NotNull(restored.PrepareSequencePlayback());
            Assert.Equal(Path.GetFullPath(projectPath), restored.ProjectPath);
            Assert.True(restored.IsProjectDirty);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Relink_suggestion_prefers_a_matching_relative_suffix_over_filename_only()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-relink-relative-");
        var originalDirectory = Directory.CreateDirectory(
            Path.Combine(directory.FullName, "old", "assets"));
        var originalPath = Path.Combine(originalDirectory.FullName, "camera.mp4");
        var projectPath = Path.Combine(directory.FullName, "project.clipedit");
        var matchingDirectory = Directory.CreateDirectory(
            Path.Combine(directory.FullName, "assets"));
        var matchingPath = Path.Combine(matchingDirectory.FullName, "camera.mp4");
        var filenameOnlyPath = Path.Combine(directory.FullName, "camera.mp4");
        var store = new JsonProjectStore();

        try
        {
            await File.WriteAllBytesAsync(originalPath, new byte[1_024]);
            using (var source = new MainWindowViewModel(new StubProbe(), projectStore: store))
            {
                await source.ImportFilesAsync([originalPath]);
                Assert.True(await source.SaveProjectAsync(projectPath));
            }

            File.Copy(originalPath, matchingPath);
            File.Copy(originalPath, filenameOnlyPath);
            File.Delete(originalPath);

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.False(await restored.OpenProjectWithRelinkingAsync(projectPath));

            var missing = Assert.Single(restored.MissingMediaReferences);
            Assert.Equal(Path.GetFullPath(matchingPath), missing.SuggestedPath);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\old\assets\camera.mp4", "assets/camera.mp4", 2)]
    [InlineData("/old/assets/camera.mp4", @"assets\camera.mp4", 2)]
    [InlineData(@"C:\old\one\camera.mp4", "two/camera.mp4", 1)]
    public void Relink_suffix_matching_is_platform_separator_independent(
        string originalPath,
        string relativeCandidate,
        int expectedSegments)
    {
        Assert.Equal(
            expectedSegments,
            MissingMediaSuggestionFinder.CountMatchingPathSuffix(
                originalPath,
                relativeCandidate));
    }

    [Fact]
    public async Task Relink_rejects_a_structurally_different_replacement()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-relink-mismatch-");
        var originalPath = Path.Combine(directory.FullName, "missing-video.mkv");
        var replacementPath = Path.Combine(directory.FullName, "audio-only.flac");
        var projectPath = Path.Combine(directory.FullName, "project.clipedit");
        var store = new JsonProjectStore();

        try
        {
            await File.WriteAllBytesAsync(originalPath, new byte[1_024]);
            using (var source = new MainWindowViewModel(new StubProbe(), projectStore: store))
            {
                await source.ImportFilesAsync([originalPath]);
                Assert.True(await source.SaveProjectAsync(projectPath));
            }

            File.Delete(originalPath);
            await File.WriteAllBytesAsync(replacementPath, new byte[1_024]);

            using var restored = new MainWindowViewModel(new StubProbe(), projectStore: store);
            Assert.False(await restored.OpenProjectWithRelinkingAsync(projectPath));
            var missing = Assert.Single(restored.MissingMediaReferences);

            Assert.False(await restored.RelinkMissingMediaAsync(missing, replacementPath));
            Assert.True(restored.HasPendingMissingMedia);
            Assert.Empty(restored.MediaItems);
            Assert.Contains("no video stream", restored.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Recovery_only_opens_after_explicit_user_action()
    {
        var directory = Directory.CreateTempSubdirectory("clipedit-recover-explicit-");
        var sourcePath = Path.Combine(directory.FullName, "source.mkv");
        var recoveryPath = Path.Combine(directory.FullName, $"{Guid.NewGuid():N}.recovery.clipedit");
        var store = new JsonProjectStore();

        try
        {
            await File.WriteAllBytesAsync(sourcePath, new byte[1_024]);
            using (var source = new MainWindowViewModel(new StubProbe(), projectStore: store))
            {
                await source.ImportFilesAsync([sourcePath]);
                await store.SaveAsync(recoveryPath, source.CreateProjectDocument());
            }

            using var recovered = new MainWindowViewModel(
                new StubProbe(),
                projectStore: store,
                recoveryDirectory: directory.FullName);
            await recovered.DiscoverRecoveryCandidatesAsync();
            var candidate = Assert.Single(recovered.RecoveryCandidates);
            Assert.Empty(recovered.MediaItems);

            Assert.True(await recovered.OpenProjectWithRelinkingAsync(
                candidate.RecoveryPath,
                isRecovery: true,
                discardUnsavedChanges: true));

            Assert.Single(recovered.MediaItems);
            Assert.Empty(recovered.RecoveryCandidates);
            Assert.Null(recovered.ProjectPath);
            Assert.True(recovered.IsProjectDirty);
        }
        finally
        {
            directory.Delete(recursive: true);
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

        viewModel.TimelineHoverTime = 5.013;

        Assert.True(viewModel.HasTimelineHoverPreview);
        Assert.Equal(0, decoder.HoverCalls);
        await decoder.WaitForHoverCallCountAsync(1);
        Assert.Equal(1, decoder.HoverCalls);
        Assert.Equal(5.013, decoder.LastHoverTimestamp, 3);
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
    [Fact]
    public async Task Mixer_mirrors_sequence_cuts_gaps_viewport_and_segment_gain()
    {
        using var viewModel = new MainWindowViewModel(new StubProbe());
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "synced-mixer.mkv")]);
        var track = Assert.Single(viewModel.AudioTracks);
        viewModel.SequencePlayheadSeconds = 30;

        Assert.True(viewModel.SplitSelectedVideoClip());
        var secondClip = viewModel.VideoClips[1];
        Assert.True(viewModel.MoveVideoClipTo(secondClip, 40));
        viewModel.IsSequenceTimelineFreeMode = true;
        viewModel.ZoomSequenceTimeline(0.5, anchor: 35);
        viewModel.SequenceSelectionStartSeconds = 42;
        viewModel.SequenceSelectionEndSeconds = 44;
        viewModel.SequencePlayheadSeconds = 43;

        Assert.Equal(70, track.TimelineDurationSeconds);
        Assert.True(track.TimelineFreeViewport);
        Assert.Equal(viewModel.SequenceTimelineZoom, track.TimelineZoom);
        Assert.Equal(viewModel.SequenceTimelineViewportStart, track.TimelineViewportStart);
        Assert.Equal(43, track.TimelinePlayheadSeconds);
        Assert.Equal(42, track.TimelineSelectionStartSeconds);
        Assert.Equal(44, track.TimelineSelectionEndSeconds);
        Assert.Collection(
            track.TimelineSegments,
            first =>
            {
                Assert.Equal(MediaTime.Zero, first.TimelineStart);
                Assert.Equal(new MediaTime(30, 1), first.TimelineEnd);
            },
            second =>
            {
                Assert.Equal(new MediaTime(40, 1), second.TimelineStart);
                Assert.Equal(new MediaTime(70, 1), second.TimelineEnd);
            });

        var previousWaveformRevision = track.WaveformVisualRevision;
        Assert.Equal("Clip gain", track.ContextualGainLabel);
        track.ContextualGainDb = -3.5;
        Assert.Equal(-3.5, secondClip.GetAudioLaneGainDb(track.EmbeddedLaneIndex!.Value));
        Assert.Equal(0, secondClip.AudioGainDb);
        Assert.True(track.WaveformVisualRevision > previousWaveformRevision);
        Assert.True(track.SilenceTimelineSelection());
        Assert.Contains(
            new MediaRange(new MediaTime(40, 1), new MediaTime(42, 1)),
            track.TimelineKeptRanges);
        Assert.Contains(
            new MediaRange(new MediaTime(44, 1), new MediaTime(70, 1)),
            track.TimelineKeptRanges);
    }

    [Fact]
    public async Task Selected_clip_gain_is_independent_for_each_embedded_audio_lane()
    {
        var renderer = new RecordingExportRenderer();
        using var viewModel = new MainWindowViewModel(new StubProbe(), exportRenderer: renderer);
        await viewModel.ImportFilesAsync([Path.Combine(Path.GetTempPath(), "multi-lane-audio.mkv")]);
        var tracks = viewModel.AudioTracks
            .OrderBy(track => track.EmbeddedLaneIndex)
            .ToArray();
        Assert.Equal(2, tracks.Length);

        tracks[0].ContextualGainDb = -8.5;
        tracks[1].ContextualGainDb = 3.25;

        var clip = Assert.Single(viewModel.VideoClips);
        Assert.Equal(0, clip.AudioGainDb);
        Assert.Equal(-8.5, clip.GetAudioLaneGainDb(0));
        Assert.Equal(3.25, clip.GetAudioLaneGainDb(1));
        Assert.Equal(-8.5, tracks[0].ContextualGainDb);
        Assert.Equal(3.25, tracks[1].ContextualGainDb);
        Assert.Equal(
            [-8.5, 3.25],
            viewModel.PreviewAudioTracks.OrderBy(track => track.StreamIndex).Select(track => track.GainDb));

        Assert.True(viewModel.Undo());
        Assert.Equal(0, Assert.Single(viewModel.VideoClips).GetAudioLaneGainDb(0));
        Assert.Equal(0, Assert.Single(viewModel.VideoClips).GetAudioLaneGainDb(1));
        Assert.True(viewModel.Redo());
        clip = Assert.Single(viewModel.VideoClips);
        Assert.Equal(-8.5, clip.GetAudioLaneGainDb(0));
        Assert.Equal(3.25, clip.GetAudioLaneGainDb(1));

        var result = await viewModel.ExportAsync(
            Path.Combine(Path.GetTempPath(), "multi-lane-output.mp4"),
            replaceExistingDestination: false);

        Assert.NotNull(result);
        Assert.Equal(
            [-8.5, 3.25],
            Assert.Single(renderer.Plan!.VideoSegments).AudioTracks
                .OrderBy(track => track.StreamIndex)
                .Select(track => track.GainDb));
        Assert.Equal(
            new Dictionary<int, double> { [0] = -8.5, [1] = 3.25 },
            Assert.Single(viewModel.CreateProjectDocument().VideoClips!).AudioLaneGainDb);
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
                    : Path.GetFileName(sourcePath).Contains("multi-lane", StringComparison.OrdinalIgnoreCase)
                        ? CreateMultiAudioVideoProbe(sourcePath)
                    : CreateVideoProbe(sourcePath));
        }
    }

    private sealed class CompatibleCopyProbe : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateCompatibleCopyProbe(sourcePath));
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
        private double _lastHoverTimestamp;

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

        public double LastHoverTimestamp => Volatile.Read(ref _lastHoverTimestamp);

        public async Task<DecodedFrame> DecodeAsync(
            string sourcePath,
            int videoStreamIndex,
            MediaTime timestamp,
            PixelSize maximumSize,
            CancellationToken cancellationToken = default,
            bool toneMapHdr = false)
        {
            _ = toneMapHdr;
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
                Volatile.Write(ref _lastHoverTimestamp, timestamp.TotalSeconds);
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

        public async Task WaitForHoverCallCountAsync(int count)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (HoverCalls < count && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10);
            }

            Assert.True(
                HoverCalls >= count,
                $"Expected {count} hover frame requests, received {HoverCalls}.");
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

    private static MediaProbeResult CreateMultiAudioVideoProbe(string sourcePath)
    {
        var probe = CreateVideoProbe(sourcePath);
        return new MediaProbeResult(
            probe.SourcePath,
            probe.FormatName,
            probe.FormatLongName,
            probe.StartTime,
            probe.Duration,
            probe.FileSizeBytes,
            probe.BitRateBitsPerSecond,
            probe.Streams.Add(CreateAudioStream(streamIndex: 2, isDefault: false)));
    }

    private static MediaProbeResult CreateAudioProbe(string sourcePath)
    {
        return CreateProbe(sourcePath, CreateAudioStream());
    }

    private static MediaProbeResult CreateCompatibleCopyProbe(string sourcePath)
    {
        var duration = new MediaTime(60, 1);
        return new MediaProbeResult(
            sourcePath,
            "mov,mp4,m4a,3gp,3g2,mj2",
            "MP4",
            MediaTime.Zero,
            duration,
            48_000_000,
            6_400_000,
            ImmutableArray.Create<MediaStreamInfo>(
                new VideoStreamInfo(
                    0,
                    "h264",
                    null,
                    "High",
                    null,
                    null,
                    true,
                    false,
                    new MediaTime(1, 1_000),
                    MediaTime.Zero,
                    duration,
                    new PixelSize(1_920, 1_080),
                    0,
                    new FrameRate(30, 1),
                    new FrameRate(30, 1),
                    "yuv420p",
                    "1:1",
                    "16:9",
                    "tv",
                    "bt709",
                    "bt709",
                    "bt709",
                    "progressive",
                    6_208_000),
                new AudioStreamInfo(
                    1,
                    "aac",
                    null,
                    null,
                    null,
                    null,
                    true,
                    false,
                    new MediaTime(1, 48_000),
                    MediaTime.Zero,
                    duration,
                    48_000,
                    2,
                    "stereo",
                    "fltp",
                    192_000)));
    }

    private static AudioStreamInfo CreateAudioStream(int streamIndex = 1, bool isDefault = true)
    {
        return new AudioStreamInfo(
            streamIndex,
            "flac",
            null,
            null,
            null,
            null,
            isDefault,
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
