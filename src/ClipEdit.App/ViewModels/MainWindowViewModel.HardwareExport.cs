using ClipEdit.Media.Export;

namespace ClipEdit.App.ViewModels;

public sealed record ExportVideoEncoderChoice(
    ExportVideoEncoder Value,
    string DisplayName,
    bool IsAvailable,
    string Details)
{
    public string MenuText => IsAvailable ? DisplayName : $"{DisplayName} (unavailable)";
}

public sealed partial class MainWindowViewModel
{
    private static readonly IReadOnlyList<ExportVideoEncoderChoice> InitialExportVideoEncoderChoices =
    [
        new(
            ExportVideoEncoder.Software,
            "Software (x264)",
            true,
            "Built-in software encoder; compatibility baseline."),
        new(ExportVideoEncoder.NvidiaNvenc, "NVIDIA NVENC", false, "Checking availability..."),
        new(ExportVideoEncoder.IntelQuickSync, "Intel Quick Sync", false, "Checking availability..."),
        new(ExportVideoEncoder.AmdAmf, "AMD AMF", false, "Checking availability..."),
        new(ExportVideoEncoder.Vaapi, "VA-API", false, "Checking availability..."),
    ];

    private IReadOnlyList<ExportVideoEncoderChoice> _exportVideoEncoderChoices =
        InitialExportVideoEncoderChoices;
    private ExportVideoEncoderChoice _selectedExportVideoEncoder =
        InitialExportVideoEncoderChoices[0];
    private ExportVideoEncoder _preferredExportVideoEncoder = ExportVideoEncoder.Software;
    private CancellationTokenSource? _exportHardwareProbeCancellation;
    private bool _isExportHardwareProbeRunning;

    public IReadOnlyList<ExportVideoEncoderChoice> ExportVideoEncoderChoices =>
        _exportVideoEncoderChoices;

    public ExportVideoEncoderChoice SelectedExportVideoEncoder
    {
        get => _selectedExportVideoEncoder;
        set
        {
            var next = value ?? ExportVideoEncoderChoices[0];
            if (!next.IsAvailable)
            {
                StatusText = next.Details;
                OnPropertyChanged();
                return;
            }

            _preferredExportVideoEncoder = next.Value;
            if (SetProperty(ref _selectedExportVideoEncoder, next))
            {
                OnPropertyChanged(nameof(PreferredExportVideoEncoder));
                OnPropertyChanged(nameof(ExportVideoEncoderStatus));
                OnPropertyChanged(nameof(ExportVideoEncoderDescription));
                RaiseExportStateChanged();
            }
        }
    }

    public ExportVideoEncoder PreferredExportVideoEncoder => _preferredExportVideoEncoder;

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

    public bool SupportsHardwareVideoEncoding =>
        !IsGifExport && GetEffectiveExportPreset().VideoCodec == VideoCodecFamily.H264;

    public string ExportVideoEncoderStatus => IsExportHardwareProbeRunning
        ? "Testing installed hardware encoders..."
        : SupportsHardwareVideoEncoding
            ? SelectedExportVideoEncoder.Details
            : "Hardware encoding currently applies to H.264 exports only.";

    public string ExportVideoEncoderDescription => SelectedExportVideoEncoder.Details;

    internal void SelectPreferredExportVideoEncoder(ExportVideoEncoder encoder)
    {
        _preferredExportVideoEncoder = Enum.IsDefined(encoder)
            ? encoder
            : ExportVideoEncoder.Software;
        var choice = ExportVideoEncoderChoices.FirstOrDefault(candidate =>
                         candidate.Value == _preferredExportVideoEncoder && candidate.IsAvailable) ??
                     ExportVideoEncoderChoices[0];
        if (!ReferenceEquals(_selectedExportVideoEncoder, choice))
        {
            _selectedExportVideoEncoder = choice;
            OnPropertyChanged(nameof(SelectedExportVideoEncoder));
            OnPropertyChanged(nameof(PreferredExportVideoEncoder));
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

        if (probe is null)
        {
            ApplyExportHardwareCapabilities(null);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _exportHardwareProbeCancellation = cancellation;
        IsExportHardwareProbeRunning = true;
        ObserveExportHardwareCapabilitiesAsync(probe, cancellation);
    }

    private async void ObserveExportHardwareCapabilitiesAsync(
        IExportHardwareCapabilityProbe probe,
        CancellationTokenSource request)
    {
        try
        {
            var capabilities = await probe.ProbeAsync(request.Token);
            if (!request.IsCancellationRequested && ReferenceEquals(_exportHardwareProbeCancellation, request))
            {
                ApplyExportHardwareCapabilities(capabilities);
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
                ApplyExportHardwareCapabilities(null, exception.Message);
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
        string? failure = null)
    {
        _exportVideoEncoderChoices = InitialExportVideoEncoderChoices
            .Select(initial =>
            {
                if (initial.Value == ExportVideoEncoder.Software)
                {
                    return initial;
                }

                var capability = capabilities?.Get(initial.Value);
                return capability is null
                    ? initial with
                    {
                        Details = string.IsNullOrWhiteSpace(failure)
                            ? "No FFmpeg capability probe is available."
                            : $"Capability check failed: {failure}",
                    }
                    : new ExportVideoEncoderChoice(
                        capability.Encoder,
                        capability.DisplayName,
                        capability.IsAvailable,
                        capability.Details);
            })
            .ToArray();
        OnPropertyChanged(nameof(ExportVideoEncoderChoices));

        var preferred = _exportVideoEncoderChoices.FirstOrDefault(choice =>
            choice.Value == _preferredExportVideoEncoder && choice.IsAvailable);
        _selectedExportVideoEncoder = preferred ?? _exportVideoEncoderChoices[0];
        if (preferred is null)
        {
            _preferredExportVideoEncoder = ExportVideoEncoder.Software;
        }

        OnPropertyChanged(nameof(SelectedExportVideoEncoder));
        OnPropertyChanged(nameof(PreferredExportVideoEncoder));
        OnPropertyChanged(nameof(ExportVideoEncoderStatus));
        OnPropertyChanged(nameof(ExportVideoEncoderDescription));
        RaiseExportStateChanged();
    }

    private void DisposeExportHardwareCapabilityProbe()
    {
        _exportHardwareProbeCancellation?.Cancel();
        _exportHardwareProbeCancellation?.Dispose();
        _exportHardwareProbeCancellation = null;
    }
}
