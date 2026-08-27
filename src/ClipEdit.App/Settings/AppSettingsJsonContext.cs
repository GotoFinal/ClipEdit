using System.Text.Json.Serialization;
using ClipEdit.App.Updates;
using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Settings;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    WriteIndented = true)]
[JsonSerializable(typeof(CanvasInteractionSettings))]
[JsonSerializable(typeof(ExportPreferences))]
[JsonSerializable(typeof(MediaRuntimeSettings))]
[JsonSerializable(typeof(InternetMediaSettings))]
[JsonSerializable(typeof(YtDlpToolState))]
[JsonSerializable(typeof(UpdateSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
