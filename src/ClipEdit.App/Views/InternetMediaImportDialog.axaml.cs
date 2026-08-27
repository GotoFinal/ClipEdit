using Avalonia.Controls;
using Avalonia.Interactivity;
using ClipEdit.App.InternetMedia;
using ClipEdit.App.Settings;

namespace ClipEdit.App.Views;

internal sealed record InternetMediaImportDialogResult(
    InternetMediaDownloadRequest Request,
    int? PreferredVideoHeight,
    int? PreferredAudioBitrateKbps,
    int PreviewVideoHeight);

public sealed partial class InternetMediaImportDialog : Window
{
    private readonly Uri? _sourceUri;
    private readonly IInternetMediaClient? _client;
    private readonly InternetMediaSettings _settings = InternetMediaSettings.Default;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private InternetMediaInfo? _mediaInfo;

    public InternetMediaImportDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    internal InternetMediaImportDialog(
        Uri sourceUri,
        IInternetMediaClient client,
        InternetMediaSettings settings)
        : this()
    {
        _sourceUri = sourceUri ?? throw new ArgumentNullException(nameof(sourceUri));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings?.Normalize() ?? throw new ArgumentNullException(nameof(settings));
        SourceUrlText.Text = sourceUri.AbsoluteUri;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_sourceUri is null || _client is null)
        {
            return;
        }

        try
        {
            StatusText.Text = "Preparing yt-dlp and checking available formats…";
            _mediaInfo = await _client.ProbeAsync(_sourceUri, _lifetimeCancellation.Token);
            var videoChoices = _mediaInfo.CreateVideoQualityChoices();
            var audioChoices = _mediaInfo.CreateAudioQualityChoices();
            VideoQualityComboBox.ItemsSource = videoChoices;
            AudioQualityComboBox.ItemsSource = audioChoices;
            VideoQualityComboBox.SelectedItem = SelectVideoChoice(videoChoices, _settings.PreferredVideoHeight);
            AudioQualityComboBox.SelectedItem = SelectAudioChoice(audioChoices, _settings.PreferredAudioBitrateKbps);
            PreviewQualityComboBox.ItemsSource = CreatePreviewQualityChoices(_mediaInfo);
            PreviewQualityComboBox.SelectedItem = SelectPreviewQuality(
                (IReadOnlyList<int>)PreviewQualityComboBox.ItemsSource!,
                _settings.PreviewVideoHeight);
            VideoQualityComboBox.IsEnabled = videoChoices.Count > 1;
            AudioQualityComboBox.IsEnabled = audioChoices.Count > 1;
            MediaTitleText.Text = _mediaInfo.Title;
            MediaDetailText.Text = BuildMediaDetails(_mediaInfo);
            StatusText.Text = videoChoices.Count > 1 || audioChoices.Count > 1
                ? "Choose final quality. Editing starts from a streaming preview while the source downloads."
                : "One suitable format is available."
                ;
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 0;
            DownloadButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (InternetMediaException exception)
        {
            StatusText.Text = exception.Message;
            DownloadProgressBar.IsIndeterminate = false;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not inspect this link: {exception.Message}";
            DownloadProgressBar.IsIndeterminate = false;
        }
    }

    private void Download_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_client is null || _mediaInfo is null ||
            VideoQualityComboBox.SelectedItem is not InternetVideoQualityChoice videoQuality ||
            AudioQualityComboBox.SelectedItem is not InternetAudioQualityChoice audioQuality ||
            PreviewQualityComboBox.SelectedItem is not int previewHeight)
        {
            return;
        }

        Close(new InternetMediaImportDialogResult(
            new InternetMediaDownloadRequest(_mediaInfo, videoQuality, audioQuality),
            videoQuality.MaximumHeight,
            audioQuality.MaximumBitrateKbps,
            previewHeight));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        _lifetimeCancellation.Cancel();
        Close(null);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    internal static InternetVideoQualityChoice SelectVideoChoice(
        IReadOnlyList<InternetVideoQualityChoice> choices,
        int? preferredHeight)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return preferredHeight is null
            ? choices[0]
            : choices.FirstOrDefault(choice => choice.MaximumHeight == preferredHeight) ??
              choices.Where(choice => choice.MaximumHeight <= preferredHeight)
                  .OrderByDescending(choice => choice.MaximumHeight)
                  .FirstOrDefault() ??
              choices[0];
    }

    internal static InternetAudioQualityChoice SelectAudioChoice(
        IReadOnlyList<InternetAudioQualityChoice> choices,
        int? preferredBitrateKbps)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return preferredBitrateKbps is null
            ? choices[0]
            : choices.FirstOrDefault(choice => choice.MaximumBitrateKbps == preferredBitrateKbps) ??
              choices.Where(choice => choice.MaximumBitrateKbps <= preferredBitrateKbps)
                  .OrderByDescending(choice => choice.MaximumBitrateKbps)
                  .FirstOrDefault() ??
              choices[0];
    }

    internal static IReadOnlyList<int> CreatePreviewQualityChoices(InternetMediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var availableMaximum = info.Formats
            .Where(static format => format.HasVideo && format.Height is > 0)
            .Max(static format => format.Height) ?? InternetMediaSettings.DefaultPreviewVideoHeight;
        return new[] { 360, 480, 720, 1080 }
            .Where(height => height <= availableMaximum)
            .Append(Math.Min(availableMaximum, 1080))
            .Where(static height => height >= 144)
            .Distinct()
            .Order()
            .ToArray();
    }

    internal static int SelectPreviewQuality(IReadOnlyList<int> choices, int preferredHeight)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
        {
            return InternetMediaSettings.DefaultPreviewVideoHeight;
        }

        return choices.Contains(preferredHeight)
            ? preferredHeight
            : choices.Where(height => height <= preferredHeight).DefaultIfEmpty(choices[0]).Max();
    }

    private static string BuildMediaDetails(InternetMediaInfo info)
    {
        var extractor = string.IsNullOrWhiteSpace(info.Extractor) ? info.SourceUri.Host : info.Extractor;
        var duration = info.Duration is { } value ? $" · {value:hh\\:mm\\:ss}" : string.Empty;
        return $"{extractor}{duration}";
    }

}
