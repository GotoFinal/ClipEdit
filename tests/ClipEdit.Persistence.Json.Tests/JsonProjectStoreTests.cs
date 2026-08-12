using ClipEdit.Application.Projects;
using System.Text.Json.Nodes;

namespace ClipEdit.Persistence.Json.Tests;

public sealed class JsonProjectStoreTests
{
    [Fact]
    public async Task Save_and_load_round_trip_exact_rational_edits()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-{Guid.NewGuid():N}.clipedit");
        var document = CreateDocument();

        try
        {
            var store = new JsonProjectStore();
            await store.SaveAsync(path, document);

            var loaded = await store.LoadAsync(path);

            Assert.Equivalent(document, loaded, strict: true);
            Assert.Contains("sourcePath", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unknown_fields_and_schema_versions_are_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-{Guid.NewGuid():N}.clipedit");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 999,
              "projectId": "11111111-1111-1111-1111-111111111111",
              "exportPresetId": null,
              "media": [],
              "unexpected": true
            }
            """);

        try
        {
            var exception = await Assert.ThrowsAsync<ProjectStoreException>(() =>
                new JsonProjectStore().LoadAsync(path));

            Assert.Equal(ProjectStoreFailure.InvalidDocument, exception.Failure);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Repeated_save_atomically_replaces_the_previous_document()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-{Guid.NewGuid():N}.clipedit");
        try
        {
            var store = new JsonProjectStore();
            var first = CreateDocument();
            var second = first with { ExportPresetId = "webm-v1" };
            await store.SaveAsync(path, first);
            await store.SaveAsync(path, second);

            Assert.Equivalent(second, await store.LoadAsync(path), strict: true);
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(path)!,
                $".{Path.GetFileName(path)}.*.saving"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Schema_one_audio_without_placement_fields_defaults_to_timeline_zero()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-{Guid.NewGuid():N}.clipedit");
        try
        {
            var store = new JsonProjectStore();
            await store.SaveAsync(path, CreateDocument());
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["schemaVersion"] = 1;
            var audioTrack = root["media"]![0]!["audioTracks"]![0]!.AsObject();
            audioTrack.Remove("timelineOffsetNumerator");
            audioTrack.Remove("timelineOffsetDenominator");
            await File.WriteAllTextAsync(path, root.ToJsonString());

            var loaded = await store.LoadAsync(path);

            var loadedTrack = Assert.Single(Assert.Single(loaded.Media).AudioTracks!);
            Assert.Equal(0, loadedTrack.TimelineOffsetNumerator);
            Assert.Equal(1, loadedTrack.TimelineOffsetDenominator);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ProjectDocument CreateDocument()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "source with spaces.mkv");
        var mediaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var clipId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        return new ProjectDocument(
            ProjectDocument.CurrentSchemaVersion,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "mp4-compatible-v1",
            [
                new ProjectMediaDocument(
                    sourcePath,
                    1_448_587_333,
                    1_920,
                    1_080,
                    420,
                    0,
                    1_080,
                    1_080,
                    60_001,
                    1_000,
                    [
                        new ProjectRangeDocument(0, 1, 10_001, 1_000),
                        new ProjectRangeDocument(20_001, 1_000, 60_001, 1_000),
                    ],
                    [
                        new ProjectAudioTrackDocument(
                            1,
                            -4.5,
                            false,
                            60_001,
                            1_000,
                            [new ProjectRangeDocument(0, 1, 60_001, 1_000)],
                            13,
                            4),
                    ],
                    mediaId),
            ],
            [
                new ProjectVideoClipDocument(
                    clipId,
                    mediaId,
                    10_001,
                    1_000,
                    20_001,
                    1_000,
                    0,
                    1,
                    60_001,
                    1_000,
                    420,
                    0,
                    1_080,
                    1_080),
            ],
            new ProjectCropSettingsDocument("1-1", true));
    }
}
