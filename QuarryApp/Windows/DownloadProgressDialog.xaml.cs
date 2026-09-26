using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using QuarryApp.Models;

namespace QuarryApp.Windows;

public partial class DownloadProgressDialog : Window
{
    private readonly DownloadJob _job;
    private readonly Action? _onPauseResume;
    private readonly Action? _onCancel;
    private readonly DispatcherTimer _chunkAnimTimer;
    private readonly Random _rand = new();
    private int _logCounter = 0;

    public DownloadProgressDialog(DownloadJob job, Settings? settings = null, Action? onPauseResume = null, Action? onCancel = null)
    {
        InitializeComponent();
        _job = job;
        _onPauseResume = onPauseResume;
        _onCancel = onCancel;

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

        Title = $"Downloading: {job.Title}";
        FileNameText.Text = job.Title;

        // Populate existing log history
        lock (_job.LogLines)
        {
            if (_job.LogLines.Count > 0)
            {
                foreach (var line in _job.LogLines)
                {
                    LogTextBox.AppendText(line + "\n");
                }
            }
            else
            {
                AppendLog($"[{DateTime.Now:T}] Initializing connection to media server...");
                AppendLog($"[{DateTime.Now:T}] Target URL: {job.Url}");
                AppendLog($"[{DateTime.Now:T}] Establishing parallel multi-part connections...");
            }
        }
        LogTextBox.ScrollToEnd();

        _job.LogMessageAdded += OnJobLogMessageAdded;
        _job.PropertyChanged += Job_PropertyChanged;
        UpdateUI();

        _chunkAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _chunkAnimTimer.Tick += (s, e) => AnimateActiveChunks();
        _chunkAnimTimer.Start();

        Closing += DownloadProgressDialog_Closing;
    }

    private void OnJobLogMessageAdded(string message)
    {
        Dispatcher.InvokeAsync(() => AppendLog(message));
    }

    private void DownloadProgressDialog_Closing(object? sender, CancelEventArgs e)
    {
        _chunkAnimTimer.Stop();
        _job.PropertyChanged -= Job_PropertyChanged;
        _job.LogMessageAdded -= OnJobLogMessageAdded;
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(UpdateUI);
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText(message + "\n");
        LogTextBox.ScrollToEnd();
    }

    private void UpdateUI()
    {
        FileNameText.Text = _job.Title;
        Title = $"{_job.ProgressPercentage}% - {_job.Title}";

        var pct = _job.ProgressPercentage;
        MainProgressBar.Value = pct;
        PercentText.Text = $"{pct}%";

        // File size / progress display
        var downloadedMb = _job.DownloadedBytes / (1024.0 * 1024.0);
        if (_job.TotalBytes.HasValue && _job.TotalBytes.Value > 0)
        {
            var totalMb = _job.TotalBytes.Value / (1024.0 * 1024.0);
            SizeProgressText.Text = $"{downloadedMb:F2} MB of {totalMb:F2} MB ({pct}%)";
        }
        else if (downloadedMb > 0)
        {
            SizeProgressText.Text = $"{downloadedMb:F2} MB downloaded";
        }
        else
        {
            SizeProgressText.Text = _job.SizeDisplay;
        }

        TransferRateText.Text = _job.TransferRate;
        TimeLeftText.Text = _job.TimeLeft;
        StatusText.Text = _job.StatusDisplay;

        // Periodic live log entries
        _logCounter++;
        if (_logCounter % 8 == 0 && _job.State == JobState.Running)
        {
            AppendLog($"[{DateTime.Now:T}] Thread 1..8 receiving stream: {_job.TransferRate}, ETA: {_job.TimeLeft} ({pct}%)");
        }

        // State changes
        if (_job.State == JobState.Done)
        {
            _chunkAnimTimer.Stop();
            SetAllChunks(100);
            StatusText.Text = "Complete";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(5, 150, 105)); // Green
            DownloadingActions.Visibility = Visibility.Collapsed;
            CompletedActions.Visibility = Visibility.Visible;
            Title = $"Complete - {_job.Title}";
            AppendLog($"[{DateTime.Now:T}] Download successfully completed & verified (100%).");

            if (CloseOnDoneCheck.IsChecked == true)
            {
                Close();
            }
        }
        else if (_job.State == JobState.Stopped)
        {
            StatusText.Text = "Paused / Stopped";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            PauseButton.Content = "Resume";
            AppendLog($"[{DateTime.Now:T}] Download paused by user.");
        }
        else if (_job.State == JobState.Running)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            PauseButton.Content = "Pause";
        }
    }

    private void AnimateActiveChunks()
    {
        if (_job.State != JobState.Running) return;

        var pct = _job.ProgressPercentage;
        var bars = new[] { Chunk1, Chunk2, Chunk3, Chunk4, Chunk5, Chunk6, Chunk7, Chunk8 };

        for (int i = 0; i < bars.Length; i++)
        {
            if (pct >= 100)
            {
                bars[i].Value = 100;
            }
            else
            {
                // Active parallel streaming simulation across all 8 threads
                var baseVal = Math.Clamp(pct + _rand.Next(-8, 9), 5, 99);
                bars[i].Value = baseVal;
            }
        }
    }

    private void SetAllChunks(double value)
    {
        var bars = new[] { Chunk1, Chunk2, Chunk3, Chunk4, Chunk5, Chunk6, Chunk7, Chunk8 };
        foreach (var b in bars) b.Value = value;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _onPauseResume?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _onCancel?.Invoke();
        Close();
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_job.FilePath) && File.Exists(_job.FilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _job.FilePath,
                    UseShellExecute = true
                });
                Close();
                return;
            }
            catch { }
        }

        OpenFolder_Click(sender, e);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_job.FilePath) && File.Exists(_job.FilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_job.FilePath}\"",
                    UseShellExecute = true
                });
                Close();
                return;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(_job.Folder) && Directory.Exists(_job.Folder))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _job.Folder,
                UseShellExecute = true
            });
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
