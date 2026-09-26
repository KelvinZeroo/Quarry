using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QuarryApp.Core;
using QuarryApp.Models;

namespace QuarryApp.Windows;

public partial class OptionsDialog : Window
{
    private readonly Settings _settings;

    public OptionsDialog(Settings settings)
    {
        InitializeComponent();
        _settings = settings;

        // Load values
        ClipboardCatchCheck.IsChecked = _settings.CatchClipboard;
        AutoScanRootCheck.IsChecked = _settings.AutoScan;
        PortTextBox.Text = _settings.Port.ToString();
        OutDirTextBox.Text = _settings.OutDir;
        WorkersTextBox.Text = _settings.Workers.ToString();
        TimeoutTextBox.Text = _settings.TimeoutSeconds.ToString();
        RetriesTextBox.Text = _settings.Retries.ToString();
        DelayTextBox.Text = _settings.DelaySeconds.ToString();
        MaxScanTextBox.Text = _settings.MaxScan.ToString();
        AskAboveTextBox.Text = _settings.AskAbove.ToString();

        EnableSpeedLimitCheck.IsChecked = _settings.EnableSpeedLimit;
        SpeedLimitTextBox.Text = _settings.SpeedLimitKbps.ToString();

        AutoExtractCheck.IsChecked = _settings.AutoExtractArchives;
        SchedulerEnabledCheck.IsChecked = _settings.SchedulerEnabled;
        StartTimeTextBox.Text = _settings.SchedulerStartTime;
        StopTimeTextBox.Text = _settings.SchedulerStopTime;

        // BitTorrent & P2P settings
        TorrentPortTextBox.Text = _settings.TorrentPort.ToString();
        MaxPeersTextBox.Text = _settings.MaxTorrentPeers.ToString();
        UPnPCheck.IsChecked = _settings.EnableUPnP;
        DHTCheck.IsChecked = _settings.EnableDHT;
        PeXCheck.IsChecked = _settings.EnablePeX;
        AssociateMagnetCheck.IsChecked = _settings.AssociateMagnet;
        AssociateTorrentCheck.IsChecked = _settings.AssociateTorrent;
        TrackersTextBox.Text = _settings.DefaultTrackers;

        // Populate Font Families
        PopulateFontFamilies();
    }

    private void PopulateFontFamilies()
    {
        var fontList = new List<string> { "Manrope", "Segoe UI Variable Text", "Segoe UI", "Tahoma", "Inter", "Arial", "Roboto", "Open Sans", "Consolas" };

        foreach (var font in Fonts.SystemFontFamilies)
        {
            var name = font.Source;
            if (!fontList.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                fontList.Add(name);
            }
        }

        FontFamilyComboBox.ItemsSource = fontList;
        FontFamilyComboBox.Text = string.IsNullOrWhiteSpace(_settings.FontFamily) ? "Manrope" : _settings.FontFamily;

        // Set font size combo
        foreach (ComboBoxItem item in FontSizeComboBox.Items)
        {
            if (double.TryParse(item.Content.ToString(), out var sz) && Math.Abs(sz - _settings.FontSize) < 0.1)
            {
                FontSizeComboBox.SelectedItem = item;
                break;
            }
        }

        UpdatePreview();
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void ResetFontDefault_Click(object sender, RoutedEventArgs e)
    {
        FontFamilyComboBox.Text = "Manrope";
        foreach (ComboBoxItem item in FontSizeComboBox.Items)
        {
            if (item.Content.ToString() == "12")
            {
                FontSizeComboBox.SelectedItem = item;
                break;
            }
        }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (PreviewHeading == null || PreviewBody == null) return;

        var fontName = FontFamilyComboBox.Text;
        if (FontFamilyComboBox.SelectedItem != null)
        {
            fontName = FontFamilyComboBox.SelectedItem.ToString();
        }

        if (string.IsNullOrWhiteSpace(fontName)) fontName = "Manrope";

        double fontSize = 12.0;
        if (FontSizeComboBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Content.ToString(), out var sz))
        {
            fontSize = sz;
        }

        try
        {
            var ff = new FontFamily(fontName);
            PreviewHeading.FontFamily = ff;
            PreviewBody.FontFamily = ff;
            PreviewBody.FontSize = fontSize;
            PreviewHeading.FontSize = fontSize + 1;
        }
        catch { }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Default Download Folder",
            InitialDirectory = Directory.Exists(OutDirTextBox.Text) ? OutDirTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == true)
        {
            OutDirTextBox.Text = dialog.FolderName;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.CatchClipboard = ClipboardCatchCheck.IsChecked == true;
        _settings.AutoScan = AutoScanRootCheck.IsChecked == true;

        if (int.TryParse(PortTextBox.Text, out var port)) _settings.Port = port;
        if (!string.IsNullOrWhiteSpace(OutDirTextBox.Text)) _settings.OutDir = OutDirTextBox.Text.Trim();
        if (int.TryParse(WorkersTextBox.Text, out var workers)) _settings.Workers = Math.Clamp(workers, 1, 32);
        if (int.TryParse(TimeoutTextBox.Text, out var timeout)) _settings.TimeoutSeconds = timeout;
        if (int.TryParse(RetriesTextBox.Text, out var retries)) _settings.Retries = retries;
        if (double.TryParse(DelayTextBox.Text, out var delay)) _settings.DelaySeconds = delay;
        if (int.TryParse(MaxScanTextBox.Text, out var maxScan)) _settings.MaxScan = maxScan;
        if (int.TryParse(AskAboveTextBox.Text, out var askAbove)) _settings.AskAbove = askAbove;

        _settings.EnableSpeedLimit = EnableSpeedLimitCheck.IsChecked == true;
        if (long.TryParse(SpeedLimitTextBox.Text, out var speedLimit)) _settings.SpeedLimitKbps = Math.Max(0, speedLimit);

        _settings.AutoExtractArchives = AutoExtractCheck.IsChecked == true;
        _settings.SchedulerEnabled = SchedulerEnabledCheck.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(StartTimeTextBox.Text)) _settings.SchedulerStartTime = StartTimeTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(StopTimeTextBox.Text)) _settings.SchedulerStopTime = StopTimeTextBox.Text.Trim();

        // BitTorrent & P2P settings
        if (int.TryParse(TorrentPortTextBox.Text, out var tPort)) _settings.TorrentPort = Math.Clamp(tPort, 1024, 65535);
        if (int.TryParse(MaxPeersTextBox.Text, out var mPeers)) _settings.MaxTorrentPeers = Math.Clamp(mPeers, 5, 500);
        _settings.EnableUPnP = UPnPCheck.IsChecked == true;
        _settings.EnableDHT = DHTCheck.IsChecked == true;
        _settings.EnablePeX = PeXCheck.IsChecked == true;
        _settings.AssociateMagnet = AssociateMagnetCheck.IsChecked == true;
        _settings.AssociateTorrent = AssociateTorrentCheck.IsChecked == true;
        _settings.DefaultTrackers = TrackersTextBox.Text;

        if (_settings.AssociateMagnet) ProtocolAssociation.RegisterMagnetProtocol();
        if (_settings.AssociateTorrent) ProtocolAssociation.RegisterTorrentExtension();

        // Font settings
        var fontName = FontFamilyComboBox.Text;
        if (FontFamilyComboBox.SelectedItem != null)
        {
            fontName = FontFamilyComboBox.SelectedItem.ToString();
        }
        if (!string.IsNullOrWhiteSpace(fontName)) _settings.FontFamily = fontName.Trim();

        if (FontSizeComboBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Content.ToString(), out var sz))
        {
            _settings.FontSize = sz;
        }

        DialogResult = true;
        Close();
    }

    private void ApplyAssociations_Click(object sender, RoutedEventArgs e)
    {
        var magnetOk = ProtocolAssociation.RegisterMagnetProtocol();
        var torrentOk = ProtocolAssociation.RegisterTorrentExtension();

        if (magnetOk && torrentOk)
        {
            MessageBox.Show(
                "Quarry has been successfully registered as the default handler for magnet: links and .torrent files in Windows!",
                "Protocol Association",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                "Windows protocol association partially registered. Make sure Quarry has standard user registry access.",
                "Protocol Association",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SettingsNavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelGeneral == null || PanelAppearance == null || PanelDownloads == null ||
            PanelConnection == null || PanelBitTorrent == null || PanelScheduler == null || PanelSiteGrabber == null)
        {
            return;
        }

        PanelGeneral.Visibility = Visibility.Collapsed;
        PanelAppearance.Visibility = Visibility.Collapsed;
        PanelDownloads.Visibility = Visibility.Collapsed;
        PanelConnection.Visibility = Visibility.Collapsed;
        PanelBitTorrent.Visibility = Visibility.Collapsed;
        PanelScheduler.Visibility = Visibility.Collapsed;
        PanelSiteGrabber.Visibility = Visibility.Collapsed;

        switch (SettingsNavList.SelectedIndex)
        {
            case 0: PanelGeneral.Visibility = Visibility.Visible; break;
            case 1: PanelAppearance.Visibility = Visibility.Visible; break;
            case 2: PanelDownloads.Visibility = Visibility.Visible; break;
            case 3: PanelConnection.Visibility = Visibility.Visible; break;
            case 4: PanelBitTorrent.Visibility = Visibility.Visible; break;
            case 5: PanelScheduler.Visibility = Visibility.Visible; break;
            case 6: PanelSiteGrabber.Visibility = Visibility.Visible; break;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
