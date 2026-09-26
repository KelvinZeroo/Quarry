using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuarryApp.Adapters;
using QuarryApp.Core;
using QuarryApp.Models;
using YoutubeExplode;

namespace QuarryApp.Windows;

public enum DownloadMode
{
    Auto,
    VideoStream,
    ScrapeImages,
    DirectFile
}

public partial class AddDownloadDialog : Window
{
    private readonly Settings? _settings;
    private readonly MediaStreamExtractor _extractor;
    private readonly YoutubeClient _youtubeClient = new();
    private DispatcherTimer? _debounceTimer;

    public string DownloadUrl => UrlTextBox.Text.Trim();
    public VideoStreamQuality? SelectedQuality => QualityComboBox.SelectedItem as VideoStreamQuality;
    public string SavePath
    {
        get
        {
            var raw = SaveAsTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return _settings?.OutDir ?? "";
            if (Directory.Exists(raw)) return raw;
            var dir = Path.GetDirectoryName(raw);
            return !string.IsNullOrWhiteSpace(dir) ? dir : raw;
        }
    }
    public string SelectedFileName => Path.GetFileName(SaveAsTextBox.Text.Trim());
    public bool StartImmediately { get; private set; } = true;

    public DownloadMode SelectedMode
    {
        get
        {
            if (ModeVideoRadio?.IsChecked == true) return DownloadMode.VideoStream;
            if (ModeScrapeRadio?.IsChecked == true) return DownloadMode.ScrapeImages;
            if (ModeDirectRadio?.IsChecked == true) return DownloadMode.DirectFile;
            return DownloadMode.Auto;
        }
    }

    public bool ForceScan => SelectedMode == DownloadMode.ScrapeImages;
    public bool AutoScan => SelectedMode == DownloadMode.Auto;
    public int? MaxCap => ScrapeLimitComboBox?.SelectedIndex switch
    {
        1 => 50,
        2 => 100,
        3 => 250,
        _ => null
    };

    public AddDownloadDialog(string initialUrl = "", string defaultSaveDir = "", Settings? settings = null)
    {
        InitializeComponent();
        _settings = settings;
        _extractor = new MediaStreamExtractor(new HttpClientService());

        UrlTextBox.Text = initialUrl;

        if (settings != null)
        {
            try
            {
                var fontName = string.IsNullOrWhiteSpace(settings.FontFamily) ? "Manrope" : settings.FontFamily;
                FontFamily = new FontFamily(fontName);
                if (settings.FontSize > 0) FontSize = settings.FontSize;
            }
            catch { }
        }

        AnalyzeAndRouteUrl(initialUrl);

        if (!string.IsNullOrEmpty(initialUrl) && _extractor.IsSupportedVideoUrl(initialUrl))
        {
            ProbeVideoQualities(initialUrl);
        }
    }

    public void SetUrl(string url)
    {
        if (!string.IsNullOrWhiteSpace(url) && url != UrlTextBox.Text)
        {
            UrlTextBox.Text = url;
            UrlTextBox.SelectAll();
        }
    }

    private void DownloadMode_Changed(object sender, RoutedEventArgs e)
    {
        var url = UrlTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) return;

        switch (SelectedMode)
        {
            case DownloadMode.VideoStream:
                SetCategory("Video");
                QualityGrid.Visibility = Visibility.Visible;
                ScrapeOptionsGrid.Visibility = Visibility.Collapsed;
                DetectionModeTitle.Text = "Video Stream Extraction";
                UpdateSavePathForMode(url, "Video", ".mp4");
                ProbeVideoQualities(url);
                break;

            case DownloadMode.ScrapeImages:
                SetCategory("Images");
                QualityGrid.Visibility = Visibility.Collapsed;
                ScrapeOptionsGrid.Visibility = Visibility.Visible;
                DetectionModeTitle.Text = "Page Media & Image Scraper";
                UpdateSavePathForScrape(url);
                break;

            case DownloadMode.DirectFile:
                QualityGrid.Visibility = Visibility.Collapsed;
                ScrapeOptionsGrid.Visibility = Visibility.Collapsed;
                DetectionModeTitle.Text = "Direct Multi-Part File Download";
                AnalyzeAndRouteUrl(url, forceDirect: true);
                break;

            default: // Auto
                ScrapeOptionsGrid.Visibility = Visibility.Collapsed;
                AnalyzeAndRouteUrl(url);
                if (_extractor.IsSupportedVideoUrl(url))
                {
                    ProbeVideoQualities(url);
                }
                else
                {
                    QualityGrid.Visibility = Visibility.Collapsed;
                }
                break;
        }
    }

    private void SetCategory(string category)
    {
        foreach (ComboBoxItem item in CategoryComboBox.Items)
        {
            if (string.Equals(item.Content?.ToString(), category, StringComparison.OrdinalIgnoreCase))
            {
                CategoryComboBox.SelectedItem = item;
                break;
            }
        }
    }

    private void UpdateSavePathForMode(string url, string category, string ext)
    {
        var baseOut = _settings?.OutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var catDir = Path.Combine(baseOut, category);
        var filename = "download" + ext;
        try
        {
            var uri = new Uri(url);
            var last = Path.GetFileNameWithoutExtension(uri.LocalPath);
            if (!string.IsNullOrEmpty(last) && last != "/" && last != "view_video")
            {
                filename = StringUtils.SanitizeFilename(last, 60) + ext;
            }
            else
            {
                filename = $"Video_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            }
        }
        catch { }
        SaveAsTextBox.Text = Path.Combine(catDir, filename);
    }

    private void UpdateSavePathForScrape(string url)
    {
        var baseOut = _settings?.OutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var targetDir = FileRouter.RouteDestinationPath(baseOut, url);
        SaveAsTextBox.Text = targetDir;
    }

    private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var url = UrlTextBox.Text.Trim();
        if (SelectedMode == DownloadMode.Auto)
        {
            AnalyzeAndRouteUrl(url);
        }

        _debounceTimer?.Stop();
        if (SelectedMode == DownloadMode.VideoStream || (SelectedMode == DownloadMode.Auto && _extractor.IsSupportedVideoUrl(url)))
        {
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _debounceTimer.Tick += (s, args) =>
            {
                _debounceTimer.Stop();
                ProbeVideoQualities(url);
            };
            _debounceTimer.Start();
        }
        else
        {
            QualityGrid.Visibility = Visibility.Collapsed;
        }
    }

    private async void ProbeVideoQualities(string url)
    {
        QualityGrid.Visibility = Visibility.Visible;
        ProbingIndicator.Visibility = Visibility.Visible;
        QualityComboBox.ItemsSource = null;

        try
        {
            // 1. If YouTube, get real title
            if (YouTubeAdapter.IsYouTubeUrl(url))
            {
                var videoId = YouTubeAdapter.ExtractVideoId(url);
                try
                {
                    var video = await _youtubeClient.Videos.GetAsync(videoId);
                    var cleanTitle = StringUtils.SanitizeFilename(video.Title, 80);
                    var baseOut = _settings?.OutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var videoDir = Path.Combine(baseOut, "Video");
                    SaveAsTextBox.Text = Path.Combine(videoDir, $"{cleanTitle}.mp4");
                    DetectionModeTitle.Text = cleanTitle;
                }
                catch { }
            }

            var qualities = await Task.Run(async () => await _extractor.ExtractQualitiesAsync(url));
            if (qualities.Count > 0)
            {
                QualityComboBox.ItemsSource = qualities;
                QualityComboBox.SelectedIndex = 0;
            }
        }
        catch { }
        finally
        {
            ProbingIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityComboBox.SelectedItem is VideoStreamQuality quality)
        {
            var ext = quality.Extension;
            var currentPath = SaveAsTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(currentPath))
            {
                var dir = Path.GetDirectoryName(currentPath) ?? "";
                var nameWithoutExt = Path.GetFileNameWithoutExtension(currentPath);
                SaveAsTextBox.Text = Path.Combine(dir, $"{nameWithoutExt}{ext}");
            }

            if (quality.ContentLength.HasValue && quality.ContentLength.Value > 0)
            {
                var bytes = quality.ContentLength.Value;
                FileSizeLabel.Text = bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB" :
                                    bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F2} MB" :
                                    $"{bytes / 1024.0:F1} KB";
            }
        }
    }

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryComboBox.SelectedItem is ComboBoxItem item && item.Content is string cat)
        {
            var baseOut = _settings?.OutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var catDir = Path.Combine(baseOut, cat);
            var filename = Path.GetFileName(SaveAsTextBox.Text.Trim());
            if (string.IsNullOrEmpty(filename)) filename = "download.file";
            SaveAsTextBox.Text = Path.Combine(catDir, filename);
        }
    }

    private void AnalyzeAndRouteUrl(string url, bool forceDirect = false)
    {
        var category = FileRouter.GetCategoryName(url);
        SetCategory(category);

        var baseOut = _settings?.OutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var targetDir = FileRouter.RouteDestinationPath(baseOut, url);

        if (!forceDirect && YouTubeAdapter.IsYouTubeUrl(url))
        {
            DetectionModeTitle.Text = "YouTube Video Stream";
            SaveAsTextBox.Text = Path.Combine(targetDir, "YouTube_Video.mp4");
        }
        else if (!forceDirect && (url.Contains("pornhub.", StringComparison.OrdinalIgnoreCase) ||
                                  url.Contains("xvideos.", StringComparison.OrdinalIgnoreCase) ||
                                  url.Contains("xhamster.", StringComparison.OrdinalIgnoreCase) ||
                                  url.Contains("spankbang.", StringComparison.OrdinalIgnoreCase) ||
                                  url.Contains("eporner.", StringComparison.OrdinalIgnoreCase)))
        {
            DetectionModeTitle.Text = "Adult Video Stream Engine";
            SaveAsTextBox.Text = Path.Combine(targetDir, "Adult_Video.mp4");
        }
        else if (!forceDirect && url.Contains("erome.com", StringComparison.OrdinalIgnoreCase))
        {
            DetectionModeTitle.Text = "EroMe Album & Video Scraper";
            SaveAsTextBox.Text = targetDir;
        }
        else if (!forceDirect && (url.Contains("pornpics.com", StringComparison.OrdinalIgnoreCase) ||
                                  url.Contains("imagefap.com", StringComparison.OrdinalIgnoreCase) ||
                                  url.Contains("rule34", StringComparison.OrdinalIgnoreCase)))
        {
            DetectionModeTitle.Text = "Media Gallery & Album Crawler";
            SaveAsTextBox.Text = targetDir;
        }
        else if (TorrentDownloader.IsTorrentUrl(url))
        {
            var meta = TorrentDownloader.ParseMagnetUri(url);
            DetectionModeTitle.Text = $"BitTorrent / Magnet Stream ({meta.DisplayName})";
            var filename = meta.DisplayName;
            if (url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                try { filename = Path.GetFileName(new Uri(url).LocalPath); } catch { }
            }
            SaveAsTextBox.Text = Path.Combine(targetDir, filename);
            if (meta.ExactLength.HasValue && meta.ExactLength.Value > 0)
            {
                var bytes = meta.ExactLength.Value;
                FileSizeLabel.Text = bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB" :
                                    bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F2} MB" :
                                    $"{bytes / 1024.0:F1} KB";
            }
        }
        else if (url.Contains(".zip") || url.Contains(".rar") || url.Contains(".7z") || url.Contains(".iso"))
        {
            var fileName = "archive.zip";
            try { fileName = Path.GetFileName(new Uri(url).LocalPath); } catch { }
            DetectionModeTitle.Text = "Compressed Archive Download";
            SaveAsTextBox.Text = Path.Combine(targetDir, fileName);
        }
        else
        {
            var fileName = "download.file";
            try
            {
                var uri = new Uri(url);
                var seg = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrEmpty(seg)) fileName = seg;
            }
            catch { }

            DetectionModeTitle.Text = "Direct Web Download";
            SaveAsTextBox.Text = Path.Combine(targetDir, fileName);
        }
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Select Destination Location",
            FileName = Path.GetFileName(SaveAsTextBox.Text.Trim()),
            InitialDirectory = SavePath
        };

        if (dialog.ShowDialog() == true)
        {
            SaveAsTextBox.Text = dialog.FileName;
        }
    }

    private void StartDownload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlTextBox.Text))
        {
            MessageBox.Show("Please enter a valid URL.", "Quarry", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StartImmediately = AutoStartCheck.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void DownloadLater_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlTextBox.Text))
        {
            MessageBox.Show("Please enter a valid URL.", "Quarry", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StartImmediately = false;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

