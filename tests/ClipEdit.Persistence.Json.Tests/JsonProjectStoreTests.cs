using ClipEdit.Application.Projects;

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

    private static ProjectDocument CreateDocument()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "source with spaces.mkv");
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
                    ]),
            ]);
    }
}
