using ClipEdit.Application.Export;
using ClipEdit.Domain.Geometry;
using ClipEdit.Media.Export;

namespace ClipEdit.App.ViewModels;

public enum ExportDestinationMode
{
    File,
    Clipboard,
    FileAndClipboard,
}

public sealed record ExportDestinationChoice(
    ExportDestinationMode Value,
    string DisplayName)
{
    public static ExportDestinationChoice File { get; } = new(
        ExportDestinationMode.File,
        "File");

    public static ExportDestinationChoice Clipboard { get; } = new(
        ExportDestinationMode.Clipboard,
        "Clipboard");

    public static ExportDestinationChoice FileAndClipboard { get; } = new(
        ExportDestinationMode.FileAndClipboard,
        "File + clipboard");

    public static IReadOnlyList<ExportDestinationChoice> All { get; } =
        [File, Clipboard, FileAndClipboard];

    public static ExportDestinationChoice FromValue(ExportDestinationMode value) => value switch
    {
        ExportDestinationMode.Clipboard => Clipboard,
        ExportDestinationMode.FileAndClipboard => FileAndClipboard,
        _ => File,
    };
}

public sealed record ExportQualityChoice(
    ExportQualityMode Value,
    string DisplayName)
{
    public static ExportQualityChoice MatchSource { get; } = new(
        ExportQualityMode.MatchSource,
        "Match input");

    public static ExportQualityChoice Custom { get; } = new(
        ExportQualityMode.Custom,
        "Custom");

    public static IReadOnlyList<ExportQualityChoice> All { get; } =
        [MatchSource, Custom];

    public static ExportQualityChoice FromValue(ExportQualityMode value) => value switch
    {
        ExportQualityMode.Custom => Custom,
        _ => MatchSource,
    };
}

public sealed record ExportEncodingSpeedChoice(
    ExportEncodingSpeed Value,
    string DisplayName,
    string Description)
{
    public static ExportEncodingSpeedChoice Faster { get; } = new(
        ExportEncodingSpeed.Faster,
        "Faster",
        "Encodes faster, usually with a larger file at the same visual quality.");

    public static ExportEncodingSpeedChoice Balanced { get; } = new(
        ExportEncodingSpeed.Balanced,
        "Balanced",
        "Balances encoding time and compression efficiency.");

    public static ExportEncodingSpeedChoice SmallerFile { get; } = new(
        ExportEncodingSpeed.SmallerFile,
        "Smaller file",
        "Encodes more slowly for better compression efficiency.");

    public static IReadOnlyList<ExportEncodingSpeedChoice> All { get; } =
        [Faster, Balanced, SmallerFile];

    public static ExportEncodingSpeedChoice FromValue(ExportEncodingSpeed value) => value switch
    {
        ExportEncodingSpeed.Faster => Faster,
        ExportEncodingSpeed.SmallerFile => SmallerFile,
        _ => Balanced,
    };
}

public sealed record ExportHardwareAccelerationChoice(
    ExportHardwareAcceleration Value,
    string DisplayName,
    string Description)
{
    public static ExportHardwareAccelerationChoice Software { get; } = new(
        ExportHardwareAcceleration.Software,
        "Software",
        "Predictable CPU decoding; often fastest when export filters run on the CPU.");

    public static ExportHardwareAccelerationChoice Automatic { get; } = new(
        ExportHardwareAcceleration.Automatic,
        "Auto",
        "Lets FFmpeg select a hardware decoder when one is suitable.");

    public static ExportHardwareAccelerationChoice Vulkan { get; } = new(
        ExportHardwareAcceleration.Vulkan,
        "Vulkan",
        "Forces Vulkan decoding; can help decode-heavy sources but frame transfers can make it slower.");

    public static IReadOnlyList<ExportHardwareAccelerationChoice> All { get; } =
        [Software, Automatic, Vulkan];

    public static ExportHardwareAccelerationChoice FromValue(ExportHardwareAcceleration value) => value switch
    {
        ExportHardwareAcceleration.Automatic => Automatic,
        ExportHardwareAcceleration.Vulkan => Vulkan,
        _ => Software,
    };
}

public sealed partial class MainWindowViewModel
{
    private static readonly double[] ExportPlaybackSpeedSnapValues =
    [
        .. new[]
        {
            1, 10, 25, 50, 75, 100, 125, 150, 175, 200, 250, 300, 400, 500,
            750, 1_000, 1_500, 2_000, 3_000, 4_000, 5_000, 7_500, 10_000,
        }.Select(PlaybackSpeedPercentToSliderValue),
    ];

    public IReadOnlyList<ExportDestinationChoice> ExportDestinationChoices =>
        ExportDestinationChoice.All;

    public IReadOnlyList<ExportQualityChoice> ExportQualityChoices =>
        ExportQualityChoice.All;

    public IReadOnlyList<ExportEncodingSpeedChoice> ExportEncodingSpeedChoices =>
        ExportEncodingSpeedChoice.All;

    public IReadOnlyList<ExportHardwareAccelerationChoice> ExportHardwareAccelerationChoices =>
        ExportHardwareAccelerationChoice.All;

    public IReadOnlyList<double> ExportPlaybackSpeedSliderSnapValues =>
        ExportPlaybackSpeedSnapValues;

    public ExportDestinationChoice SelectedExportDestination
    {
        get => _selectedExportDestination;
        set
        {
            var next = value ?? ExportDestinationChoice.File;
            if (SetProperty(ref _selectedExportDestination, next))
            {
                OnPropertyChanged(nameof(ExportActionText));
                OnPropertyChanged(nameof(ExportDestination));
                OnPropertyChanged(nameof(ExportSettingsSummary));
            }
        }
    }

    public ExportDestinationMode ExportDestination => SelectedExportDestination.Value;

    public ExportQualityChoice SelectedExportQuality
    {
        get => _selectedExportQuality;
        set
        {
            var next = value ?? ExportQualityChoice.MatchSource;
            if (SetProperty(ref _selectedExportQuality, next))
            {
                OnPropertyChanged(nameof(ExportQualityMode));
                OnPropertyChanged(nameof(UsesCustomExportQuality));
                OnPropertyChanged(nameof(UsesMatchedInputQuality));
                RaiseExportStateChanged();
            }
        }
    }

    public ExportQualityMode ExportQualityMode => SelectedExportQuality.Value;

    public ExportEncodingSpeedChoice SelectedExportEncodingSpeed
    {
        get => _selectedExportEncodingSpeed;
        set
        {
            var next = value ?? ExportEncodingSpeedChoice.Balanced;
            if (SetProperty(ref _selectedExportEncodingSpeed, next))
            {
                OnPropertyChanged(nameof(ExportEncodingSpeed));
                OnPropertyChanged(nameof(ExportEncodingSpeedDescription));
                RaiseExportStateChanged();
            }
        }
    }

    public ExportEncodingSpeed ExportEncodingSpeed => SelectedExportEncodingSpeed.Value;

    public string ExportEncodingSpeedDescription => SelectedExportEncodingSpeed.Description;

    public ExportHardwareAccelerationChoice SelectedExportHardwareAcceleration
    {
        get => _selectedExportHardwareAcceleration;
        set
        {
            var next = value ?? ExportHardwareAccelerationChoice.Software;
            if (SetProperty(ref _selectedExportHardwareAcceleration, next))
            {
                OnPropertyChanged(nameof(ExportHardwareAcceleration));
                OnPropertyChanged(nameof(ExportHardwareAccelerationDescription));
                RaiseExportStateChanged();
            }
        }
    }

    public ExportHardwareAcceleration ExportHardwareAcceleration =>
        SelectedExportHardwareAcceleration.Value;

    public string ExportHardwareAccelerationDescription =>
        SelectedExportHardwareAcceleration.Description;

    public bool UsesCustomExportQuality =>
        IsGifExport || ExportQualityMode == ClipEdit.Media.Export.ExportQualityMode.Custom;

    public bool UsesMatchedInputQuality =>
        !IsGifExport && ExportQualityMode == ClipEdit.Media.Export.ExportQualityMode.MatchSource;

    public bool RememberExportAdjustments
    {
        get => _rememberExportAdjustments;
        set => SetProperty(ref _rememberExportAdjustments, value);
    }

    public string ExportActionText => ExportDestination == ExportDestinationMode.Clipboard
        ? "Copy"
        : "Export";

    public int ExportScalePercent
    {
        get => _exportScalePercent;
        set
        {
            var next = Math.Clamp(value, 10, 100);
            if (SetProperty(ref _exportScalePercent, next))
            {
                OnPropertyChanged(nameof(ExportScaleSliderValue));
                RaiseExportStateChanged();
            }
        }
    }

    public double ExportScaleSliderValue
    {
        get => ExportScalePercent;
        set => ExportScalePercent = (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public int ExportQuality
    {
        get => _exportQuality;
        set
        {
            var next = Math.Clamp(value, 1, 100);
            if (SetProperty(ref _exportQuality, next))
            {
                OnPropertyChanged(nameof(ExportQualitySliderValue));
                RaiseExportStateChanged();
            }
        }
    }

    public double ExportQualitySliderValue
    {
        get => ExportQuality;
        set => ExportQuality = (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public int GifFrameRate
    {
        get => _gifFrameRate;
        set
        {
            var next = Math.Clamp(value, 1, 60);
            if (SetProperty(ref _gifFrameRate, next))
            {
                OnPropertyChanged(nameof(GifFrameRateSliderValue));
                RaiseExportStateChanged();
            }
        }
    }

    public double GifFrameRateSliderValue
    {
        get => GifFrameRate;
        set => GifFrameRate = (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public int ExportPlaybackSpeedPercent
    {
        get => _exportPlaybackSpeedPercent;
        set
        {
            var next = Math.Clamp(
                value,
                ExportEncodingSettings.MinimumPlaybackSpeedPercent,
                ExportEncodingSettings.MaximumPlaybackSpeedPercent);
            if (SetProperty(ref _exportPlaybackSpeedPercent, next))
            {
                OnPropertyChanged(nameof(ExportPlaybackSpeedSliderValue));
                RaiseExportStateChanged();
            }
        }
    }

    public double ExportPlaybackSpeedSliderValue
    {
        get => PlaybackSpeedPercentToSliderValue(ExportPlaybackSpeedPercent);
        set => ExportPlaybackSpeedPercent = SliderValueToPlaybackSpeedPercent(value);
    }

    internal static double PlaybackSpeedPercentToSliderValue(int playbackSpeedPercent) =>
        Math.Log10(Math.Clamp(
            playbackSpeedPercent,
            ExportEncodingSettings.MinimumPlaybackSpeedPercent,
            ExportEncodingSettings.MaximumPlaybackSpeedPercent)) * 25;

    internal static int SliderValueToPlaybackSpeedPercent(double sliderValue) =>
        (int)Math.Round(
            Math.Pow(10, Math.Clamp(sliderValue, 0, 100) / 25),
            MidpointRounding.AwayFromZero);

    public bool IsGifExport => GetEffectiveExportPreset().VideoCodec == VideoCodecFamily.Gif;

    public string ExportOutputSizeText
    {
        get
        {
            var size = CurrentExportEncodingSettings.CalculateOutputSize(
                CanvasCrop.ExportSize,
                GetEffectiveExportPreset().RequiresEvenDimensions);
            return $"{size.Width} × {size.Height}";
        }
    }

    public string ExportSettingsSummary
    {
        get
        {
            var destination = ExportDestination switch
            {
                ExportDestinationMode.Clipboard => "Clipboard · ",
                ExportDestinationMode.FileAndClipboard => "File + clipboard · ",
                _ => string.Empty,
            };
            var speed = ExportPlaybackSpeedPercent == 100
                ? string.Empty
                : $" · {ExportPlaybackSpeedPercent}% speed";
            var quality = IsGifExport || ExportQualityMode == ClipEdit.Media.Export.ExportQualityMode.Custom
                ? $"quality {ExportQuality}%"
                : "match input quality";
            var performance = IsGifExport
                ? string.Empty
                : $" · {SelectedExportEncodingSpeed.DisplayName.ToLowerInvariant()} encode" +
                  $" · {SelectedExportHardwareAcceleration.DisplayName} decode" +
                  (SupportsHardwareVideoEncoding
                      ? $" · {SelectedExportVideoEncoder.DisplayName}"
                      : string.Empty);
            return IsGifExport
                ? $"{destination}{ExportOutputSizeText} · {quality} · {GifFrameRate} fps{speed}"
                : $"{destination}{ExportOutputSizeText} · {quality}{speed}{performance}";
        }
    }

    public bool IsPacketCopyExport
    {
        get
        {
            var slices = GetSequenceExportSlices();
            var preset = ResolveSelectedExportPreset(slices);
            return ResolveExportStrategyDecision(slices, preset).Strategy is
                ExportStrategy.StreamCopy or ExportStrategy.EditListStreamCopy or ExportStrategy.ConcatStreamCopy;
        }
    }

    public bool IsVideoStreamCopyExport
    {
        get
        {
            var slices = GetSequenceExportSlices();
            var preset = ResolveSelectedExportPreset(slices);
            return ResolveExportStrategyDecision(slices, preset).Strategy == ExportStrategy.VideoStreamCopy;
        }
    }

    public bool IsBoundaryGopExport
    {
        get
        {
            var slices = GetSequenceExportSlices();
            var preset = ResolveSelectedExportPreset(slices);
            return ResolveExportStrategyDecision(slices, preset).Strategy == ExportStrategy.BoundaryGop;
        }
    }

    public bool IsFullReencodeExport =>
        !IsPacketCopyExport && !IsVideoStreamCopyExport && !IsBoundaryGopExport;

    public string ExportMethodTitle
    {
        get
        {
            var slices = GetSequenceExportSlices();
            var preset = ResolveSelectedExportPreset(slices);
            return ResolveExportStrategyDecision(slices, preset).Strategy switch
            {
                ExportStrategy.StreamCopy => "Fast packet copy",
                ExportStrategy.EditListStreamCopy => "Fast MP4 packet trim",
                ExportStrategy.ConcatStreamCopy => "Fast packet copy",
                ExportStrategy.VideoStreamCopy => "Fast video copy",
                ExportStrategy.BoundaryGop => "Experimental Boundary-GOP",
                _ => "Full re-encode",
            };
        }
    }

    public string ExportMethodDetails
    {
        get
        {
            var slices = GetSequenceExportSlices();
            var preset = ResolveSelectedExportPreset(slices);
            var decision = ResolveExportStrategyDecision(slices, preset);
            return decision.Strategy switch
            {
                ExportStrategy.StreamCopy =>
                    "Fastest option ready. No re-encoding.",
                ExportStrategy.EditListStreamCopy =>
                    "Fast trim ready. Video and audio will be copied without re-encoding.",
                ExportStrategy.ConcatStreamCopy =>
                    "Fast join ready. Compatible clips will be copied without re-encoding.",
                ExportStrategy.VideoStreamCopy =>
                    FormatExportActions(
                        "Video will be copied; only audio will be re-encoded.",
                        "To avoid audio re-encoding:",
                        decision.Reasons),
                ExportStrategy.BoundaryGop =>
                    "Faster trim ready. Only the cut edges will be encoded; the rest will be copied.",
                _ => FormatExportActions(
                    "This export needs re-encoding.",
                    "For a faster export:",
                    decision.Reasons),
            };
        }
    }

    private static string FormatExportActions(
        string summary,
        string heading,
        IReadOnlyList<string> actions) =>
        actions.Count == 0
            ? summary
            : summary +
              Environment.NewLine +
              heading +
              Environment.NewLine +
              string.Join(
                  Environment.NewLine,
                  actions.Distinct().Select(static action => $"• {action}"));

    public bool CanApplyFastCopySettings
    {
        get
        {
            var slices = GetSequenceExportSlices();
            if (slices.Count != 1 || slices[0].Clip.Source.Media?.Probe.VideoStreams.FirstOrDefault() is not { } video)
            {
                return false;
            }

            var preset = ResolveSelectedExportPreset(slices);
            var decision = ResolveExportStrategyDecision(slices, preset);
            if (decision.Strategy is ExportStrategy.StreamCopy or ExportStrategy.ConcatStreamCopy ||
                (!SourceVideoCodecMatches(video.CodecName, VideoCodecFamily.H264) &&
                 !SourceVideoCodecMatches(video.CodecName, VideoCodecFamily.Vp9) &&
                 !SourceVideoCodecMatches(video.CodecName, VideoCodecFamily.Av1)))
            {
                return false;
            }

            var actionable = PacketCopyBlocker.Quality |
                             PacketCopyBlocker.ExportScale |
                             PacketCopyBlocker.ExportSpeed |
                             PacketCopyBlocker.Format |
                             PacketCopyBlocker.VideoCodec |
                             PacketCopyBlocker.FrameRate;
            var sourceAudioIsCopyable = slices[0].Clip.Source.Media!.Probe.AudioStreams
                .Any(audio =>
                    SourceAudioCodecMatches(audio.CodecName, AudioCodecFamily.Aac) ||
                    SourceAudioCodecMatches(audio.CodecName, AudioCodecFamily.Opus));
            if (sourceAudioIsCopyable)
            {
                actionable |= PacketCopyBlocker.AudioCodec;
            }

            return (decision.Blockers & actionable) != 0;
        }
    }

    public bool ApplyFastCopySettings()
    {
        if (!CanApplyFastCopySettings)
        {
            return false;
        }

        SelectedExportPreset = BuiltInExportPresets.MatchInput;
        SelectedExportQuality = ExportQualityChoice.MatchSource;
        ExportScalePercent = ExportEncodingSettings.DefaultScalePercent;
        ExportPlaybackSpeedPercent = ExportEncodingSettings.DefaultPlaybackSpeedPercent;

        var slices = GetSequenceExportSlices();
        var decision = ResolveExportStrategyDecision(slices, ResolveSelectedExportPreset(slices));
        StatusText = decision.Strategy switch
        {
            ExportStrategy.StreamCopy =>
                "Export settings fixed. Fast packet copy is ready.",
            ExportStrategy.EditListStreamCopy =>
                "Export settings fixed. Fast MP4 trim is ready.",
            ExportStrategy.ConcatStreamCopy =>
                "Export settings fixed. Fast joining is ready.",
            ExportStrategy.VideoStreamCopy =>
                "Export settings fixed. Video can now be copied.",
            ExportStrategy.BoundaryGop =>
                "Export settings fixed. Fast edge encoding is ready.",
            _ => $"Export settings fixed. Next: {decision.Reasons.FirstOrDefault() ?? "This edit still needs re-encoding."}",
        };
        RaiseExportStateChanged();
        return true;
    }

    private ExportEncodingSettings CurrentExportEncodingSettings => new(
        ExportQuality,
        ExportScalePercent,
        GifFrameRate,
        ExportPlaybackSpeedPercent,
        IsGifExport ? ClipEdit.Media.Export.ExportQualityMode.Custom : ExportQualityMode,
        ExportEncodingSpeed,
        ExportHardwareAcceleration,
        EffectiveExportVideoEncoder);

    private void ResetTransientExportAdjustments()
    {
        SelectedExportQuality = ExportQualityChoice.FromValue(ExportEncodingSettings.DefaultQualityMode);
        ExportQuality = ExportEncodingSettings.DefaultQuality;
        ExportScalePercent = ExportEncodingSettings.DefaultScalePercent;
        GifFrameRate = ExportEncodingSettings.DefaultGifFrameRate;
        ExportPlaybackSpeedPercent = ExportEncodingSettings.DefaultPlaybackSpeedPercent;
    }

    public bool HasExportBlockingIssue => !IsExporting && ExportAvailabilityText != "Ready to export";

    public bool CanFixExportCompatibility => TryGetCompatibleOutputSize(out _);

    public string ExportCompatibilityActionText =>
        TryGetCompatibleOutputSize(out var size)
            ? $"Use {size.Width} × {size.Height}"
            : "Fix export";

    public bool MakeExportCropCompatible()
    {
        var slices = GetSequenceExportSlices();
        if (slices.Count == 0 || !TryGetCompatibleOutputSize(out var compatibleSize))
        {
            StatusText = ExportAvailabilityText;
            return false;
        }

        var crop = CanvasCrop;
        var x = Math.Clamp(crop.X, 0, crop.SourceSize.Width - compatibleSize.Width);
        var y = Math.Clamp(crop.Y, 0, crop.SourceSize.Height - compatibleSize.Height);
        CanvasCrop = new CropRegion(
            crop.SourceSize,
            x,
            y,
            compatibleSize.Width,
            compatibleSize.Height);
        StatusText = $"Crop changed to {compatibleSize.Width} × {compatibleSize.Height}; export is ready";
        RaiseExportStateChanged();
        return true;
    }

    private bool TryGetCompatibleOutputSize(out PixelSize compatibleSize)
    {
        compatibleSize = default;
        if (!GetEffectiveExportPreset().RequiresEvenDimensions)
        {
            return false;
        }

        var slices = GetSequenceExportSlices();
        if (slices.Count == 0)
        {
            return false;
        }

        var crop = CanvasCrop;
        var output = CurrentExportEncodingSettings.CalculateOutputSize(crop.ExportSize, false);
        if ((output.Width & 1) == 0 && (output.Height & 1) == 0)
        {
            return false;
        }

        compatibleSize = new PixelSize(
            ToEvenDimension(crop.Width, crop.SourceSize.Width),
            ToEvenDimension(crop.Height, crop.SourceSize.Height));
        return true;
    }

    private static int ToEvenDimension(int value, int maximum)
    {
        if ((value & 1) == 0)
        {
            return value;
        }

        return value > 1 ? value - 1 : Math.Min(2, maximum);
    }
}
