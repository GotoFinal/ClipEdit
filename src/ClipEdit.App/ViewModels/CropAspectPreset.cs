namespace ClipEdit.App.ViewModels;

public sealed record CropAspectPreset(
    string Id,
    string DisplayName,
    int WidthUnits,
    int HeightUnits,
    bool IsFullFrame = false)
{
    public string RatioText => IsFullFrame ? "Source" : $"{WidthUnits}:{HeightUnits}";
}

public static class BuiltInCropAspectPresets
{
    public static CropAspectPreset Source { get; } =
        new("source", "Source · full frame", 1, 1, IsFullFrame: true);

    public static CropAspectPreset Landscape169 { get; } =
        new("16-9", "16:9 · landscape video", 16, 9);

    public static CropAspectPreset Portrait916 { get; } =
        new("9-16", "9:16 · vertical video", 9, 16);

    public static CropAspectPreset Square { get; } =
        new("1-1", "1:1 · square", 1, 1);

    public static CropAspectPreset Portrait45 { get; } =
        new("4-5", "4:5 · portrait feed", 4, 5);

    public static CropAspectPreset Classic43 { get; } =
        new("4-3", "4:3 · classic video", 4, 3);

    public static CropAspectPreset Screen1610 { get; } =
        new("16-10", "16:10 · computer screen", 16, 10);

    public static CropAspectPreset Photo32 { get; } =
        new("3-2", "3:2 · photo / screen", 3, 2);

    public static CropAspectPreset Ultrawide219 { get; } =
        new("21-9", "21:9 · ultrawide", 21, 9);

    public static CropAspectPreset Cinema239 { get; } =
        new("239-100", "2.39:1 · cinema", 239, 100);

    public static IReadOnlyList<CropAspectPreset> All { get; } =
    [
        Source,
        Landscape169,
        Portrait916,
        Square,
        Portrait45,
        Classic43,
        Screen1610,
        Photo32,
        Ultrawide219,
        Cinema239,
    ];
}
