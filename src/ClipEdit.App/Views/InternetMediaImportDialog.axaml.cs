using Avalonia.Controls;
using Avalonia.Interactivity;
using ClipEdit.App.InternetMedia;
using ClipEdit.App.Settings;

namespace ClipEdit.App.Views;

internal sealed record InternetMediaImportDialogResult(
    string LocalPath,
    int? PreferredVideoHeight,
    int? PreferredAudioBitrateKbps);

public sealed partial class InternetMediaImportDialog : Window
{
    private readonly Uri? _sourceUri;
    private readonly IInternetMediaClient? _client;
    private readonly InternetMediaSettings _settings = InternetMediaSettings.Default;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private InternetMediaInfo? _mediaInfo;
    private bool _isDownloading;

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
            VideoQualityComboBox.IsEnabled = videoChoices.Count > 1;
            AudioQualityComboBox.IsEnabled = audioChoices.Count > 1;
            MediaTitleText.Text = _mediaInfo.Title;
            MediaDetailText.Text = BuildMediaDetails(_mediaInfo);
            StatusText.Text = videoChoices.Count > 1 || audioChoices.Count > 1
                ? "Choose quality, then download the local editing copy."
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

    private async void Download_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_isDownloading || _client is null || _mediaInfo is null ||
            VideoQualityComboBox.SelectedItem is not InternetVideoQualityChoice videoQuality ||
            AudioQualityComboBox.SelectedItem is not InternetAudioQualityChoice audioQuality)
        {
            return;
        }

        _isDownloading = true;
        DownloadButton.IsEnabled = false;
        VideoQualityComboBox.IsEnabled = false;
        AudioQualityComboBox.IsEnabled = false;
        DownloadProgressBar.IsIndeterminate = true;
        StatusText.Text = "Starting download…";
        try
        {
            var request = new InternetMediaDownloadRequest(_mediaInfo, videoQuality, audioQuality);
            var progress = new Progress<InternetMediaDownloadProgress>(UpdateDownloadProgress);
            var result = await _client.DownloadAsync(request, progress, _lifetimeCancellation.Token);
            Close(new InternetMediaImportDialogResult(
                result.LocalPath,
                videoQuality.MaximumHeight,
                audioQuality.MaximumBitrateKbps));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (InternetMediaException exception)
        {
            StatusText.Text = exception.Message;
            DownloadButton.IsEnabled = true;
            VideoQualityComboBox.IsEnabled = VideoQualityComboBox.ItemCount > 1;
            AudioQualityComboBox.IsEnabled = AudioQualityComboBox.ItemCount > 1;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not download this media: {exception.Message}";
            DownloadButton.IsEnabled = true;
            VideoQualityComboBox.IsEnabled = VideoQualityComboBox.ItemCount > 1;
            AudioQualityComboBox.IsEnabled = AudioQualityComboBox.ItemCount > 1;
        }
        finally
        {
            _isDownloading = false;
        }
    }

    private void UpdateDownloadProgress(InternetMediaDownloadProgress progress)
    {
        DownloadProgressBar.IsIndeterminate = progress.Fraction is null;
        if (progress.Fraction is { } fraction)
        {
            DownloadProgressBar.Value = fraction;
        }

        var percentage = progress.Fraction is { } value ? $"{value:P0}" : "Downloading";
        var bytes = progress.DownloadedBytes is { } downloaded && progress.TotalBytes is { } total
            ? $" · {FormatBytes(downloaded)} / {FormatBytes(total)}"
            : progress.DownloadedBytes is { } partial
                ? $" · {FormatBytes(partial)}"
                : string.Empty;
        var remaining = progress.Remaining is { } eta
            ? $" · about {FormatRemaining(eta)} left"
            : string.Empty;
        StatusText.Text = $"{percentage}{bytes}{remaining}";
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

    private static string BuildMediaDetails(InternetMediaInfo info)
    {
        var extractor = string.IsNullOrWhiteSpace(info.Extractor) ? info.SourceUri.Host : info.Extractor;
        var duration = info.Duration is { } value ? $" · {value:hh\\:mm\\:ss}" : string.Empty;
        return $"{extractor}{duration}";
    }

    private static string FormatBytes(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        return bytes >= megabyte ? $"{bytes / megabyte:0.#} MB" : $"{Math.Max(1, bytes / 1024d):0.#} KB";
    }

    private static string FormatRemaining(TimeSpan remaining) => remaining.TotalMinutes >= 1
        ? $"{Math.Ceiling(remaining.TotalMinutes):0} min"
        : $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0} sec";
}
