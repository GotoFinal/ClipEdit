using ClipEdit.Media.Export;

namespace ClipEdit.App.ViewModels;

public sealed class ExportVideoEncoderChoice(
    ExportVideoEncoder value,
    string displayName,
    bool isAvailable,
    string details) : ViewModelBase
{
    private string _displayName = displayName;
    private bool _isAvailable = isAvailable;
    private string _details = details;

    public ExportVideoEncoder Value { get; } = value;

    public string DisplayName => _displayName;

    public bool IsAvailable => _isAvailable;

    public string Details => _details;

    public string MenuText => IsAvailable ? DisplayName : $"{DisplayName} (unavailable)";

    internal void Update(string nextDisplayName, bool nextIsAvailable, string nextDetails)
    {
        var menuChanged = !string.Equals(_displayName, nextDisplayName, StringComparison.Ordinal) ||
                          _isAvailable != nextIsAvailable;
        SetProperty(ref _displayName, nextDisplayName, nameof(DisplayName));
        SetProperty(ref _isAvailable, nextIsAvailable, nameof(IsAvailable));
        SetProperty(ref _details, nextDetails, nameof(Details));
        if (menuChanged)
        {
            OnPropertyChanged(nameof(MenuText));
        }
    }
}

public sealed partial class MainWindowViewModel
{
    private readonly IReadOnlyList<ExportVideoEncoderChoice> _exportVideoEncoderChoices =
        CreateInitialExportVideoEncoderChoices();
    private ExportVideoEncoderChoice? _selectedExportVideoEncoder;
    private ExportVideoEncoder _preferredExportVideoEncoder = ExportEncodingSettings.DefaultVideoEncoder;
    private ExportVideoEncoder _automaticExportVideoEncoder = ExportVideoEncoder.Software;
    private IExportHardwareCapabilityProbe? _exportHardwareCapabilityProbe;
    private VideoCodecFamily? _exportHardwareProbeCodec;
    private CancellationTokenSource? _exportHardwareProbeCancellation;
    private bool _isExportHardwareProbeRunning;

    public IReadOnlyList<ExportVideoEncoderChoice> ExportVideoEncoderChoices =>
        _exportVideoEncoderChoices;

    public ExportVideoEncoderChoice SelectedExportVideoEncoder
    {
        get => _selectedExportVideoEncoder ?? _exportVideoEncoderChoices[1];
        set
        {
            var next = value ?? ExportVideoEncoderChoices.First(choice =>
                choice.Value == ExportVideoEncoder.Software);
            if (!next.IsAvailable)
            {
                StatusText = next.Details;
                OnPropertyChanged();
                return;
            }

            _preferredExportVideoEncoder = next.Value;
            if (SetProperty(ref _selectedExportVideoEncoder, next, nameof(SelectedExportVideoEncoder)))
            {
                OnPropertyChanged(nameof(PreferredExportVideoEncoder));
                OnPropertyChanged(nameof(EffectiveExportVideoEncoder));
                OnPropertyChanged(nameof(ExportVideoEncoderStatus));
                OnPropertyChanged(nameof(ExportVideoEncoderDescription));
                RaiseExportStateChanged();
            }
        }
    }

    public ExportVideoEncoder PreferredExportVideoEncoder => _preferredExportVideoEncoder;

    public ExportVideoEncoder EffectiveExportVideoEncoder =>
        SelectedExportVideoEncoder.Value == ExportVideoEncoder.Automatic
            ? _automaticExportVideoEncoder
            : SelectedExportVideoEncoder.Value;

    public bool IsExportHardwareProbeRunning
    {
        get => _isExportHardwareProbeRunning;
        private set
        {
            if (SetProperty(ref _isExportHardwareProbeRunning, value))
            {
                OnPropertyChanged(nameof(ExportVideoEncoderStatus));
            }
        }
    }

    public bool SupportsHardwareVideoEncoding => !IsGifExport;

    public string ExportVideoEncoderStatus => IsExportHardwareProbeRunning
        ? $"Testing {CurrentExportVideoCodecLabel} encoders..."
        : SupportsHardwareVideoEncoding
            ? SelectedExportVideoEncoder.Details
            : "GIF export does not use a selectable video encoder.";

    public string ExportVideoEncoderDescription => SelectedExportVideoEncoder.Details;

    internal void SelectPreferredExportVideoEncoder(ExportVideoEncoder encoder)
    {
        _preferredExportVideoEncoder = Enum.IsDefined(encoder)
            ? encoder
            : ExportVideoEncoder.Software;
        var choice = ExportVideoEncoderChoices.FirstOrDefault(candidate =>
                         candidate.Value == _preferredExportVideoEncoder && candidate.IsAvailable) ??
                     ExportVideoEncoderChoices.First(candidate =>
                         candidate.Value == ExportVideoEncoder.Software);
        if (!ReferenceEquals(SelectedExportVideoEncoder, choice))
        {
            _selectedExportVideoEncoder = choice;
            OnPropertyChanged(nameof(SelectedExportVideoEncoder));
            OnPropertyChanged(nameof(PreferredExportVideoEncoder));
            OnPropertyChanged(nameof(EffectiveExportVideoEncoder));
            OnPropertyChanged(nameof(ExportVideoEncoderStatus));
            OnPropertyChanged(nameof(ExportVideoEncoderDescription));
            RaiseExportStateChanged();
        }
    }

    internal void ConfigureExportHardwareCapabilityProbe(IExportHardwareCapabilityProbe? probe)
    {
        _exportHardwareProbeCancellation?.Cancel();
        _exportHardwareProbeCancellation?.Dispose();
        _exportHardwareProbeCancellation = null;
        _exportHardwareCapabilityProbe = probe;
        _exportHardwareProbeCodec = null;

        if (probe is null)
        {
            ApplyExportHardwareCapabilities(
                null,
                GetEffectiveExportPreset().VideoCodec);
            return;
        }

        RefreshExportHardwareCapabilityProbe();
    }

    private void RefreshExportHardwareCapabilityProbe()
    {
        var videoCodec = GetEffectiveExportPreset().VideoCodec;
        if (videoCodec == VideoCodecFamily.Gif)
        {
            _exportHardwareProbeCancellation?.Cancel();
            _exportHardwareProbeCancellation?.Dispose();
            _exportHardwareProbeCancellation = null;
            _exportHardwareProbeCodec = videoCodec;
            IsExportHardwareProbeRunning = false;
            return;
        }
        if (_exportHardwareProbeCodec == videoCodec)
        {
            return;
        }

        _exportHardwareProbeCancellation?.Cancel();
        _exportHardwareProbeCancellation?.Dispose();
        _exportHardwareProbeCancellation = null;
        _exportHardwareProbeCodec = videoCodec;
        if (_exportHardwareCapabilityProbe is null)
        {
            ApplyExportHardwareCapabilities(null, videoCodec);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _exportHardwareProbeCancellation = cancellation;
        IsExportHardwareProbeRunning = true;
        ObserveExportHardwareCapabilitiesAsync(
            _exportHardwareCapabilityProbe,
            videoCodec,
            cancellation);
    }

    private async void ObserveExportHardwareCapabilitiesAsync(
        IExportHardwareCapabilityProbe probe,
        VideoCodecFamily videoCodec,
        CancellationTokenSource request)
    {
        try
        {
            var capabilities = await probe.ProbeAsync(videoCodec, request.Token);
            if (!request.IsCancellationRequested && ReferenceEquals(_exportHardwareProbeCancellation, request))
            {
                ApplyExportHardwareCapabilities(capabilities, videoCodec);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!request.IsCancellationRequested && ReferenceEquals(_exportHardwareProbeCancellation, request))
            {
                ApplyExportHardwareCapabilities(null, videoCodec, exception.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_exportHardwareProbeCancellation, request))
            {
                IsExportHardwareProbeRunning = false;
            }
        }
    }

    private void ApplyExportHardwareCapabilities(
        ExportHardwareCapabilities? capabilities,
        VideoCodecFamily videoCodec,
        string? failure = null)
    {
        foreach (var choice in _exportVideoEncoderChoices)
        {
            if (choice.Value == ExportVideoEncoder.Automatic)
            {
                if (capabilities is null)
                {
                    choice.Update(
                        "Auto (timed)",
                        false,
                        string.IsNullOrWhiteSpace(failure)
                            ? "No FFmpeg capability probe is available."
                            : $"Capability check failed: {failure}");
                }
                else
                {
                    var fastest = capabilities.FastestAvailable(videoCodec);
                    choice.Update(
                        "Auto (timed)",
                        fastest.IsAvailable,
                        $"Uses {fastest.DisplayName}, the fastest encoder in this runtime self-test.");
                }

                continue;
            }

            var capability = capabilities?.Get(choice.Value, videoCodec);
            if (capability is null)
            {
                choice.Update(
                    InitialEncoderDisplayName(choice.Value),
                    choice.Value == ExportVideoEncoder.Software,
                    string.IsNullOrWhiteSpace(failure)
                        ? "No FFmpeg capability probe is available."
                        : $"Capability check failed: {failure}");
            }
            else
            {
                choice.Update(
                    capability.DisplayName,
                    capability.IsAvailable,
                    capability.Details);
            }
        }

        var nextAutomaticEncoder = capabilities?.FastestAvailable(videoCodec).Encoder ??
                                   ExportVideoEncoder.Software;

        var preferred = _exportVideoEncoderChoices.FirstOrDefault(choice =>
            choice.Value == _preferredExportVideoEncoder && choice.IsAvailable);
        var nextSelected = preferred ?? _exportVideoEncoderChoices.First(choice =>
            choice.Value == ExportVideoEncoder.Software);

        _automaticExportVideoEncoder = nextAutomaticEncoder;
        if (!ReferenceEquals(SelectedExportVideoEncoder, nextSelected))
        {
            _selectedExportVideoEncoder = nextSelected;
            OnPropertyChanged(nameof(SelectedExportVideoEncoder));
        }

        OnPropertyChanged(nameof(PreferredExportVideoEncoder));
        OnPropertyChanged(nameof(EffectiveExportVideoEncoder));
        OnPropertyChanged(nameof(ExportVideoEncoderStatus));
        OnPropertyChanged(nameof(ExportVideoEncoderDescription));
        RaiseExportStateChanged();
    }

    private static IReadOnlyList<ExportVideoEncoderChoice> CreateInitialExportVideoEncoderChoices() =>
    [
        new(ExportVideoEncoder.Automatic, "Auto (timed)", false, "Checking encoder performance..."),
        new(ExportVideoEncoder.Software, "Software", true, "Built-in software encoder."),
        new(ExportVideoEncoder.NvidiaNvenc, "NVIDIA NVENC", false, "Checking availability..."),
        new(ExportVideoEncoder.IntelQuickSync, "Intel Quick Sync", false, "Checking availability..."),
        new(ExportVideoEncoder.AmdAmf, "AMD AMF", false, "Checking availability..."),
        new(ExportVideoEncoder.Vaapi, "VA-API", false, "Checking availability..."),
    ];

    private static string InitialEncoderDisplayName(ExportVideoEncoder encoder) => encoder switch
    {
        ExportVideoEncoder.Software => "Software",
        ExportVideoEncoder.NvidiaNvenc => "NVIDIA NVENC",
        ExportVideoEncoder.IntelQuickSync => "Intel Quick Sync",
        ExportVideoEncoder.AmdAmf => "AMD AMF",
        ExportVideoEncoder.Vaapi => "VA-API",
        ExportVideoEncoder.Automatic => "Auto (timed)",
        _ => encoder.ToString(),
    };

    private void DisposeExportHardwareCapabilityProbe()
    {
        _exportHardwareProbeCancellation?.Cancel();
        _exportHardwareProbeCancellation?.Dispose();
        _exportHardwareProbeCancellation = null;
        _exportHardwareCapabilityProbe = null;
    }

    private string CurrentExportVideoCodecLabel => GetEffectiveExportPreset().VideoCodec switch
    {
        VideoCodecFamily.H264 => "H.264",
        VideoCodecFamily.Hevc => "HEVC",
        VideoCodecFamily.Vp8 => "VP8",
        VideoCodecFamily.Vp9 => "VP9",
        VideoCodecFamily.Av1 => "AV1",
        VideoCodecFamily.Gif => "GIF",
        _ => "video",
    };
}
