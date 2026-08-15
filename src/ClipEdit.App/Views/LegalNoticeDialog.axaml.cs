using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClipEdit.App.Views;

public sealed partial class LegalNoticeDialog : Window
{
    internal const string NoticeText =
        """
        Copyright (C) 2026 ClipEdit contributors.

        ClipEdit is free software licensed under the GNU General Public License, version 3. You may redistribute and/or modify it under those terms.

        ClipEdit is provided WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU GPL for details.

        Release builds include the complete GNU GPL text, third-party notices, the exact SPDX software bill of materials, and directions to the corresponding-source archives. The source and compliance assets are offered beside the portable executable at no additional charge.

        FFmpeg, mpv/libmpv, .NET, Avalonia and their dependencies remain copyrighted by their respective authors and are distributed under the terms identified in the bundled third-party notices.
        """;

    private readonly string _licensesPath;

    public LegalNoticeDialog()
    {
        InitializeComponent();
        _licensesPath = ResolveLicensesPath();
        NoticeTextBox.Text = NoticeText;
        LicenseLocationText.Text = Directory.Exists(_licensesPath)
            ? $"Bundled license files: {_licensesPath}"
            : "Bundled license files are added to compliance-enabled release builds. See LICENSE and THIRD_PARTY_NOTICES.md in the source repository for this development build.";
        OpenLicensesButton.IsEnabled = Directory.Exists(_licensesPath);
    }

    internal static string ResolveLicensesPath()
    {
        var bundledNoticesPath = Environment.GetEnvironmentVariable(
            BundledRuntimeBootstrapper.BundledNoticesEnvironmentVariable);
        var root = string.IsNullOrWhiteSpace(bundledNoticesPath)
            ? AppContext.BaseDirectory
            : bundledNoticesPath;
        return Path.Combine(root, "licenses");
    }

    private void OpenLicenses_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _licensesPath,
                UseShellExecute = true,
            });
            ActionStatusText.Text = "Opened license folder";
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = $"Could not open folder: {exception.Message}";
        }
    }

    private async void CopyNotice_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        var clipboard = Clipboard;
        if (clipboard is null)
        {
            ActionStatusText.Text = "Clipboard is unavailable";
            return;
        }

        var item = new DataTransferItem();
        item.Set(DataFormat.Text, NoticeText);
        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);
        ActionStatusText.Text = "Notice copied";
    }

    private void Close_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close();
    }
}
