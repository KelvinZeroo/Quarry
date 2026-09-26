using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using QuarryApp.Core;
using QuarryApp.Models;
using QuarryApp.Server;
using QuarryApp.Windows;

using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuarryApp;

public partial class MainWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly Settings _settings;
    private readonly HttpClientService _httpClient;
    private readonly ObservableCollection<DownloadJob> _jobs = new();
    private readonly ICollectionView _jobsView;
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new();
    private LocalApiServer? _apiServer;
    private IntPtr _hwnd = IntPtr.Zero;
    private string _lastClipboardText = string.Empty;
    private string _currentFilterTag = "all";
    private AddDownloadDialog? _activeAddDownloadDialog;
    private QueueScheduler? _scheduler;
    private readonly string? _initialArg;

    private static readonly Regex MediaUrlPattern = new(
        @"(https?://[^\s]+(youtube\.com|youtu\.be|erome|pornpics|imagefap|rule34|createaiasian|instagram|tiktok|twitter|x\.com|pornhub|xhamster|\.jpg|\.png|\.gif|\.webp|\.mp4|\.webm|\.mkv|\.zip|\.rar|\.7z|\.pdf|\.mp3|\.exe|\.iso|\.torrent))|magnet:\?[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MainWindow(string? initialArg = null)
    {
        InitializeComponent();

        _initialArg = initialArg;
        _settings = ConfigManager.Load();
        _httpClient = new HttpClientService();

        ApplyGlobalFont();

        _jobsView = CollectionViewSource.GetDefaultView(_jobs);
        _jobsView.Filter = FilterJobs;
        JobsDataGrid.ItemsSource = _jobsView;

        WorkersStatusText.Text = $"Workers: {_settings.Workers}";

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        KeyDown += MainWindow_KeyDown;
    }

    private void ApplyGlobalFont()
    {
        var fontName = string.IsNullOrWhiteSpace(_settings.FontFamily) ? "Manrope" : _settings.FontFamily;
        var fontSize = _settings.FontSize > 0 ? _settings.FontSize : 12.0;

        try
        {
            FontFamily = new FontFamily(fontName);
            FontSize = fontSize;
        }
        catch
        {
            FontFamily = new FontFamily("Manrope, Segoe UI, sans-serif");
            FontSize = 12.0;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Restore previous download jobs & paused/completed states from disk
        var savedJobs = JobStore.LoadJobs();
        foreach (var savedJob in savedJobs)
        {
            AttachJobListeners(savedJob);
            _jobs.Add(savedJob);
        }
        UpdateCategoryBadges();
        _jobsView.Refresh();

        // 2. Start background local API server for extension
        _apiServer = new LocalApiServer(
            _settings,
            (url, payload) => Dispatcher.Invoke(() => StartNewDownload(url, _settings.OutDir)),
            GetServerStatus
        );
        _apiServer.Start();

        // 3. Attach zero-polling Win32 Clipboard Listener hook
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);

        // 4. Initialize time-window queue scheduler
        _scheduler = new QueueScheduler(
            onStartQueue: () => Dispatcher.Invoke(() => StartQueue_Click(this, new RoutedEventArgs())),
            onStopQueue: () => Dispatcher.Invoke(() => StopQueue_Click(this, new RoutedEventArgs()))
        )
        {
            IsSchedulerEnabled = _settings.SchedulerEnabled,
            ScheduledStartTime = TimeSpan.TryParse(_settings.SchedulerStartTime, out var st) ? st : null,
            ScheduledStopTime = TimeSpan.TryParse(_settings.SchedulerStopTime, out var sp) ? sp : null
        };
        _scheduler.Start();

        // 5. Ensure Magnet / Torrent Windows protocol associations if configured
        if (_settings.AssociateMagnet && !ProtocolAssociation.IsMagnetRegistered())
        {
            ProtocolAssociation.RegisterMagnetProtocol();
        }
        if (_settings.AssociateTorrent && !ProtocolAssociation.IsTorrentRegistered())
        {
            ProtocolAssociation.RegisterTorrentExtension();
        }

        // 6. Handle initial command-line argument (e.g. magnet link or .torrent file opened with Quarry)
        if (!string.IsNullOrWhiteSpace(_initialArg))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartNewDownload(_initialArg, _settings.OutDir);
            }), DispatcherPriority.Background);
        }

        // Immediate startup GC trim to minimize idle working set
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _scheduler?.Stop();
        if (_hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
        }
        _apiServer?.Stop();
        ConfigManager.Save(_settings);
        JobStore.SaveJobs(_jobs);
    }

    private void AttachJobListeners(DownloadJob job)
    {
        job.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DownloadJob.State) ||
                e.PropertyName == nameof(DownloadJob.ProgressPercentage) ||
                e.PropertyName == nameof(DownloadJob.Downloaded))
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateCategoryBadges();
                    _jobsView.Refresh();
                });
            }
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _settings.CatchClipboard)
        {
            OnClipboardUpdated();
        }
        return IntPtr.Zero;
    }

    private void OnClipboardUpdated()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(text) && text != _lastClipboardText && MediaUrlPattern.IsMatch(text))
                {
                    _lastClipboardText = text;

                    // If user minimized Quarry, do not forcefully unminimize or steal focus
                    if (WindowState != WindowState.Minimized)
                    {
                        ShowAddDownloadDialog(text);
                    }
                }
            }
        }
        catch
        {
            // Clipboard locked by another app
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            try
            {
                var text = Clipboard.GetText()?.Trim();
                if (!string.IsNullOrEmpty(text) && (text.StartsWith("http://") || text.StartsWith("https://")))
                {
                    ShowAddDownloadDialog(text);
                }
                else
                {
                    ShowAddDownloadDialog("");
                }
            }
            catch
            {
                ShowAddDownloadDialog("");
            }
        }
        else if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ShowAddDownloadDialog("");
        }
        else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            JobsDataGrid.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelected_Click(sender, e);
            e.Handled = true;
        }
    }

    private void JobsDataGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelected_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            JobsDataGrid.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyUrlContext_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            var selected = GetSelectedJobs();
            if (selected.Count == 1)
            {
                OpenSelectedFileOrFolder();
            }
            else if (selected.Count > 1)
            {
                ResumeSelected_Click(sender, e);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            RenameFileContext_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            RedownloadSelected_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            MoveJobUp_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            MoveJobDown_Click(sender, e);
            e.Handled = true;
        }
    }

    private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            // If the row clicked is not already part of the selected set, select it exclusively
            if (!row.IsSelected)
            {
                JobsDataGrid.SelectedItems.Clear();
                row.IsSelected = true;
            }
            row.Focus();
        }
    }

    private List<DownloadJob> GetSelectedJobs()
    {
        return JobsDataGrid.SelectedItems.OfType<DownloadJob>().ToList();
    }

    private void ShowAddDownloadDialog(string url)
    {
        if (_activeAddDownloadDialog != null && _activeAddDownloadDialog.IsLoaded)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                _activeAddDownloadDialog.SetUrl(url);
            }
            _activeAddDownloadDialog.Activate();
            _activeAddDownloadDialog.Focus();
            return;
        }

        var dialog = new AddDownloadDialog(url, _settings.OutDir, _settings)
        {
            Owner = this
        };
        _activeAddDownloadDialog = dialog;
        dialog.Closed += (_, _) => _activeAddDownloadDialog = null;

        if (dialog.ShowDialog() == true)
        {
            var targetUrl = dialog.DownloadUrl;
            var selectedQuality = dialog.SelectedQuality;
            var outDir = string.IsNullOrWhiteSpace(dialog.SavePath) ? _settings.OutDir : dialog.SavePath;
            var fileName = dialog.SelectedFileName;
            var expectedPath = Path.Combine(outDir, fileName);

            // Check for duplicate / existing file on disk
            if (File.Exists(expectedPath))
            {
                var warnDialog = new DuplicateWarningDialog(expectedPath, _settings)
                {
                    Owner = this
                };

                if (warnDialog.ShowDialog() == true)
                {
                    if (warnDialog.ResultAction == DuplicateAction.SaveAsNew)
                    {
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                        var ext = Path.GetExtension(fileName);
                        int counter = 1;
                        while (File.Exists(Path.Combine(outDir, $"{nameWithoutExt} ({counter}){ext}")))
                        {
                            counter++;
                        }
                        fileName = $"{nameWithoutExt} ({counter}){ext}";
                    }
                    else if (warnDialog.ResultAction == DuplicateAction.Cancel || warnDialog.ResultAction == DuplicateAction.OpenFile)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            if (dialog.StartImmediately)
            {
                StartNewDownload(targetUrl, outDir, dialog.ForceScan, dialog.AutoScan, dialog.MaxCap, selectedQuality, fileName);
            }
            else
            {
                var job = new DownloadJob
                {
                    Url = targetUrl,
                    Title = fileName,
                    Folder = outDir,
                    State = JobState.Queued,
                    SelectedQuality = selectedQuality,
                    TotalBytes = selectedQuality?.ContentLength
                };
                AttachJobListeners(job);
                _jobs.Insert(0, job);
                JobsDataGrid.SelectedItem = job;
                UpdateCategoryBadges();
                _jobsView.Refresh();
                JobStore.SaveJobs(_jobs);
            }
        }
    }

    private void StartNewDownload(string url, string outDir, bool forceScan = false, bool autoScan = true, int? maxCap = null, VideoStreamQuality? selectedQuality = null, string? customFileName = null)
    {
        var isGenericName = string.IsNullOrWhiteSpace(customFileName) ||
                            customFileName.Equals("download.file", StringComparison.OrdinalIgnoreCase) ||
                            customFileName.Equals("General", StringComparison.OrdinalIgnoreCase) ||
                            customFileName.Equals("Images", StringComparison.OrdinalIgnoreCase) ||
                            customFileName.Equals("Video", StringComparison.OrdinalIgnoreCase) ||
                            customFileName.Equals("Music", StringComparison.OrdinalIgnoreCase) ||
                            customFileName.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
                            customFileName.Equals("Compressed", StringComparison.OrdinalIgnoreCase) ||
                            customFileName.Equals("Programs", StringComparison.OrdinalIgnoreCase);

        var initialTitle = !isGenericName ? customFileName! : url;

        if (TorrentDownloader.IsTorrentUrl(url) && (isGenericName || initialTitle == url))
        {
            var meta = TorrentDownloader.ParseMagnetUri(url);
            initialTitle = meta.DisplayName;
        }

        var job = new DownloadJob
        {
            Url = url,
            Title = initialTitle,
            Folder = outDir,
            State = JobState.Queued,
            SelectedQuality = selectedQuality,
            TotalBytes = selectedQuality?.ContentLength
        };

        AttachJobListeners(job);

        _jobs.Insert(0, job);
        JobsDataGrid.SelectedItem = job;
        UpdateCategoryBadges();
        _jobsView.Refresh();
        JobStore.SaveJobs(_jobs);

        var jobSettings = new Settings
        {
            OutDir = outDir,
            Workers = _settings.Workers,
            Scan = forceScan,
            AutoScan = autoScan,
            MaxImages = maxCap,
            AskAbove = _settings.AskAbove,
            DelaySeconds = _settings.DelaySeconds,
            Retries = _settings.Retries,
            TorrentPort = _settings.TorrentPort,
            MaxTorrentPeers = _settings.MaxTorrentPeers,
            EnableDHT = _settings.EnableDHT,
            EnableUPnP = _settings.EnableUPnP,
            EnablePeX = _settings.EnablePeX,
            DefaultTrackers = _settings.DefaultTrackers,
            AutoExtractArchives = _settings.AutoExtractArchives,
            EnableSpeedLimit = _settings.EnableSpeedLimit,
            SpeedLimitKbps = _settings.SpeedLimitKbps
        };

        var cts = new CancellationTokenSource();
        _cancellations[job.Id] = cts;

        ShowProgressDialog(job);

        Task.Run(async () =>
        {
            await DownloadEngine.RunJobAsync(
                job,
                jobSettings,
                _httpClient,
                async (found) =>
                {
                    return await Dispatcher.InvokeAsync(() =>
                    {
                        var res = MessageBox.Show(
                            $"Site scan discovered {found} media items.\n\nWould you like to download all of them?",
                            "Quarry Site Scan",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Question);

                        return res == MessageBoxResult.Yes ? found : (int?)null;
                    });
                },
                cts.Token
            );

            Dispatcher.Invoke(() =>
            {
                _cancellations.Remove(job.Id);
                UpdateCategoryBadges();
                _jobsView.Refresh();
                JobStore.SaveJobs(_jobs);
            });
        });
    }

    private void RestartExistingJob(DownloadJob job)
    {
        if (_cancellations.TryGetValue(job.Id, out var existingCts))
        {
            existingCts.Cancel();
            _cancellations.Remove(job.Id);
        }

        job.State = JobState.Queued;
        job.Error = "";
        job.Downloaded = 0;
        job.Skipped = 0;
        job.Failed = 0;
        job.DownloadedBytes = 0;
        job.TransferRate = "—";
        job.TimeLeft = "—";
        job.LogLines.Clear();
        job.LogLines.Add($"[{DateTime.Now:T}] Restarting download for {job.Url}");

        var jobSettings = new Settings
        {
            OutDir = job.Folder,
            Workers = _settings.Workers,
            AskAbove = _settings.AskAbove,
            DelaySeconds = _settings.DelaySeconds,
            Retries = _settings.Retries,
            TorrentPort = _settings.TorrentPort,
            MaxTorrentPeers = _settings.MaxTorrentPeers,
            EnableDHT = _settings.EnableDHT,
            EnableUPnP = _settings.EnableUPnP,
            EnablePeX = _settings.EnablePeX,
            DefaultTrackers = _settings.DefaultTrackers,
            AutoExtractArchives = _settings.AutoExtractArchives,
            EnableSpeedLimit = _settings.EnableSpeedLimit,
            SpeedLimitKbps = _settings.SpeedLimitKbps
        };

        var cts = new CancellationTokenSource();
        _cancellations[job.Id] = cts;

        ShowProgressDialog(job);

        Task.Run(async () =>
        {
            await DownloadEngine.RunJobAsync(
                job,
                jobSettings,
                _httpClient,
                async (found) =>
                {
                    return await Dispatcher.InvokeAsync(() =>
                    {
                        var res = MessageBox.Show(
                            $"Site scan discovered {found} media items.\n\nWould you like to download all of them?",
                            "Quarry Site Scan",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Question);

                        return res == MessageBoxResult.Yes ? found : (int?)null;
                    });
                },
                cts.Token
            );

            Dispatcher.Invoke(() =>
            {
                _cancellations.Remove(job.Id);
                UpdateCategoryBadges();
                _jobsView.Refresh();
            });
        });
    }

    private void ShowProgressDialog(DownloadJob job)
    {
        var progressDialog = new DownloadProgressDialog(
            job,
            _settings,
            onPauseResume: () =>
            {
                if (job.State == JobState.Running && _cancellations.TryGetValue(job.Id, out var c))
                {
                    c.Cancel();
                    job.State = JobState.Stopped;
                }
                else if (job.State != JobState.Running)
                {
                    RestartExistingJob(job);
                }
            },
            onCancel: () =>
            {
                if (_cancellations.TryGetValue(job.Id, out var c))
                {
                    c.Cancel();
                    job.State = JobState.Stopped;
                }
            }
        )
        {
            Owner = this
        };
        progressDialog.Show();
    }

    private string _searchFilterText = string.Empty;
    private bool _isQueueRunning = false;
    private CancellationTokenSource? _queueCts;

    private void SearchFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchFilterText = SearchFilterTextBox.Text.Trim();
        _jobsView?.Refresh();
    }

    private void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isQueueRunning) return;
        _isQueueRunning = true;
        _queueCts = new CancellationTokenSource();
        QueueStatusText.Text = "Queue: Active";
        StartQueueBtn.IsEnabled = false;
        StopQueueBtn.IsEnabled = true;

        Task.Run(async () => await ProcessQueueAsync(_queueCts.Token));
    }

    private void StopQueue_Click(object sender, RoutedEventArgs e)
    {
        _isQueueRunning = false;
        _queueCts?.Cancel();
        QueueStatusText.Text = "Queue: Stopped";
        StartQueueBtn.IsEnabled = true;
        StopQueueBtn.IsEnabled = false;
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (_isQueueRunning && !ct.IsCancellationRequested)
        {
            DownloadJob? nextJob = null;
            await Dispatcher.InvokeAsync(() =>
            {
                nextJob = _jobs.FirstOrDefault(j => j.State == JobState.Queued);
            });

            if (nextJob == null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _isQueueRunning = false;
                    QueueStatusText.Text = "Queue: Idle";
                    StartQueueBtn.IsEnabled = true;
                    StopQueueBtn.IsEnabled = false;
                });
                break;
            }

            var tcs = new TaskCompletionSource<bool>();
            await Dispatcher.InvokeAsync(() =>
            {
                StartJob(nextJob, () => tcs.TrySetResult(true));
            });

            await tcs.Task;
            await Task.Delay(400, ct);
        }
    }

    private void StartJob(DownloadJob job, Action? onFinished = null)
    {
        var jobSettings = new Settings
        {
            OutDir = job.Folder,
            Workers = _settings.Workers,
            Scan = false,
            AutoScan = true,
            DelaySeconds = _settings.DelaySeconds,
            Retries = _settings.Retries,
            TorrentPort = _settings.TorrentPort,
            MaxTorrentPeers = _settings.MaxTorrentPeers,
            EnableDHT = _settings.EnableDHT,
            EnableUPnP = _settings.EnableUPnP,
            EnablePeX = _settings.EnablePeX,
            DefaultTrackers = _settings.DefaultTrackers,
            AutoExtractArchives = _settings.AutoExtractArchives,
            EnableSpeedLimit = _settings.EnableSpeedLimit,
            SpeedLimitKbps = _settings.SpeedLimitKbps
        };

        var cts = new CancellationTokenSource();
        _cancellations[job.Id] = cts;

        Task.Run(async () =>
        {
            await DownloadEngine.RunJobAsync(job, jobSettings, _httpClient, null, cts.Token);
            Dispatcher.Invoke(() =>
            {
                _cancellations.Remove(job.Id);
                UpdateCategoryBadges();
                _jobsView.Refresh();
                onFinished?.Invoke();
            });
        });
    }

    private bool FilterJobs(object item)
    {
        if (item is not DownloadJob job) return true;

        if (!string.IsNullOrEmpty(_searchFilterText))
        {
            if (!job.Title.Contains(_searchFilterText, StringComparison.OrdinalIgnoreCase) &&
                !job.Url.Contains(_searchFilterText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (_currentFilterTag.StartsWith("compressed"))
            return job.Url.Contains(".zip", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".rar", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".7z", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".tar", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".iso", StringComparison.OrdinalIgnoreCase);

        if (_currentFilterTag.StartsWith("documents"))
            return job.Url.Contains(".pdf", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".doc", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".txt", StringComparison.OrdinalIgnoreCase);

        if (_currentFilterTag.StartsWith("music"))
            return job.Url.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".flac", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".wav", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".m4a", StringComparison.OrdinalIgnoreCase);

        if (_currentFilterTag.StartsWith("programs"))
            return job.Url.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".msi", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".apk", StringComparison.OrdinalIgnoreCase);

        if (_currentFilterTag.StartsWith("video"))
            return job.Url.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains("erome", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".mkv", StringComparison.OrdinalIgnoreCase);

        if (_currentFilterTag.StartsWith("images"))
            return job.Url.Contains("pornpics", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains("imagefap", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains("rule34", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".gif", StringComparison.OrdinalIgnoreCase) ||
                   job.Url.Contains(".webp", StringComparison.OrdinalIgnoreCase);

        return _currentFilterTag switch
        {
            "unfinished" => job.State == JobState.Running || job.State == JobState.Queued,
            "finished" => job.State == JobState.Done,
            "stopped" => job.State == JobState.Stopped,
            "failed" => job.State == JobState.Error,
            "scans" => job.Url.Contains("sitemap", StringComparison.OrdinalIgnoreCase) ||
                       job.Url.Contains("createaiasian", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private void UpdateCategoryBadges()
    {
        var total = _jobs.Count;
        var inProgress = _jobs.Count(j => j.State == JobState.Running || j.State == JobState.Queued);
        var finished = _jobs.Count(j => j.State == JobState.Done);
        var stopped = _jobs.Count(j => j.State == JobState.Stopped);
        var failed = _jobs.Count(j => j.State == JobState.Error);

        if (BadgeAllCount != null) BadgeAllCount.Text = total.ToString();
        if (BadgeProgressCount != null) BadgeProgressCount.Text = inProgress.ToString();
        if (BadgeFinishedCount != null) BadgeFinishedCount.Text = finished.ToString();
        if (BadgeStoppedCount != null) BadgeStoppedCount.Text = stopped.ToString();
        if (BadgeFailedCount != null) BadgeFailedCount.Text = failed.ToString();

        if (ItemsCountText != null) ItemsCountText.Text = $"{total} item(s)";
    }

    private Dictionary<string, object> GetServerStatus()
    {
        var dict = new Dictionary<string, object>();
        foreach (var job in _jobs)
        {
            dict[job.Id] = new
            {
                id = job.Id,
                url = job.Url,
                title = job.Title,
                folder = job.Folder,
                state = job.State.ToString().ToLower(),
                total = job.Total,
                downloaded = job.Downloaded,
                skipped = job.Skipped,
                failed = job.Failed,
                error = job.Error
            };
        }
        return dict;
    }

    // Toolbar & Menu Handlers
    private void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        ShowAddDownloadDialog("");
    }

    private void ResumeSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        foreach (var job in selected)
        {
            if (job.State != JobState.Running)
            {
                RestartExistingJob(job);
            }
        }
    }

    private void StopSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        foreach (var job in selected)
        {
            if (_cancellations.TryGetValue(job.Id, out var cts))
            {
                cts.Cancel();
                _cancellations.Remove(job.Id);
            }
            job.State = JobState.Stopped;
        }
        UpdateCategoryBadges();
        _jobsView.Refresh();
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cts in _cancellations.Values)
        {
            cts.Cancel();
        }
        _cancellations.Clear();
        foreach (var job in _jobs.Where(j => j.State == JobState.Running || j.State == JobState.Queued))
        {
            job.State = JobState.Stopped;
        }
        UpdateCategoryBadges();
        _jobsView.Refresh();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        foreach (var job in selected)
        {
            if (_cancellations.TryGetValue(job.Id, out var cts))
            {
                cts.Cancel();
                _cancellations.Remove(job.Id);
            }
            _jobs.Remove(job);
        }
        UpdateCategoryBadges();
        _jobsView.Refresh();
        JobStore.SaveJobs(_jobs);
    }

    private void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        var completed = _jobs.Where(j => j.State == JobState.Done).ToList();
        foreach (var job in completed)
        {
            _jobs.Remove(job);
        }
        UpdateCategoryBadges();
        _jobsView.Refresh();
        JobStore.SaveJobs(_jobs);
    }

    private void MoveJobUp_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        var ordered = selected.OrderBy(j => _jobs.IndexOf(j)).ToList();
        foreach (var job in ordered)
        {
            var idx = _jobs.IndexOf(job);
            if (idx > 0)
            {
                _jobs.Move(idx, idx - 1);
            }
        }
        JobStore.SaveJobs(_jobs);
        _jobsView.Refresh();
    }

    private void MoveJobDown_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        var ordered = selected.OrderByDescending(j => _jobs.IndexOf(j)).ToList();
        foreach (var job in ordered)
        {
            var idx = _jobs.IndexOf(job);
            if (idx >= 0 && idx < _jobs.Count - 1)
            {
                _jobs.Move(idx, idx + 1);
            }
        }
        JobStore.SaveJobs(_jobs);
        _jobsView.Refresh();
    }

    private void MoveJobTop_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        var ordered = selected.OrderBy(j => _jobs.IndexOf(j)).ToList();
        int targetIdx = 0;
        foreach (var job in ordered)
        {
            var idx = _jobs.IndexOf(job);
            if (idx >= 0)
            {
                _jobs.Move(idx, targetIdx++);
            }
        }
        JobStore.SaveJobs(_jobs);
        _jobsView.Refresh();
    }

    private void MoveJobBottom_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        var ordered = selected.OrderByDescending(j => _jobs.IndexOf(j)).ToList();
        int targetIdx = _jobs.Count - 1;
        foreach (var job in ordered)
        {
            var idx = _jobs.IndexOf(job);
            if (idx >= 0)
            {
                _jobs.Move(idx, targetIdx--);
            }
        }
        JobStore.SaveJobs(_jobs);
        _jobsView.Refresh();
    }

    private void Grabber_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SiteGrabberDialog(_settings)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            StartNewDownload(dialog.StartUrl, _settings.OutDir, forceScan: true, maxCap: dialog.MaxScan);
        }
    }

    private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_settings.OutDir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.OutDir,
                UseShellExecute = true
            });
        }
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OptionsDialog(_settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            ConfigManager.Save(_settings);
            ApplyGlobalFont();
            WorkersStatusText.Text = $"Workers: {_settings.Workers}";

            if (_scheduler != null)
            {
                _scheduler.IsSchedulerEnabled = _settings.SchedulerEnabled;
                _scheduler.ScheduledStartTime = TimeSpan.TryParse(_settings.SchedulerStartTime, out var st) ? st : null;
                _scheduler.ScheduledStopTime = TimeSpan.TryParse(_settings.SchedulerStopTime, out var sp) ? sp : null;
            }
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Quarry Download Manager\nVersion 1.0.0 (.NET 10 WPF)\n\nHigh-performance media downloader and archiver.\nZero browser dependencies.", "About Quarry", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void JobsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedFileOrFolder();
    }

    private void OpenFileContext_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedFileOrFolder();
    }

    private void OpenSelectedFileOrFolder()
    {
        if (JobsDataGrid.SelectedItem is not DownloadJob job) return;

        // If job is running or queued, open the IDM Download Progress popup
        if (job.State == JobState.Running || job.State == JobState.Queued)
        {
            ShowProgressDialog(job);
            return;
        }

        // 1. Direct File (Video, Audio, Compressed, etc.)
        if (!string.IsNullOrEmpty(job.FilePath) && File.Exists(job.FilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = job.FilePath,
                    UseShellExecute = true
                });
                return;
            }
            catch { }
        }

        // 2. Folder
        if (!string.IsNullOrEmpty(job.Folder) && Directory.Exists(job.Folder))
        {
            var files = Directory.GetFiles(job.Folder);
            if (files.Length == 1 && File.Exists(files[0]))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = files[0],
                        UseShellExecute = true
                    });
                    return;
                }
                catch { }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = job.Folder,
                UseShellExecute = true
            });
        }
    }

    private void OpenFolderContext_Click(object sender, RoutedEventArgs e)
    {
        if (JobsDataGrid.SelectedItem is not DownloadJob job) return;

        if (!string.IsNullOrEmpty(job.FilePath) && File.Exists(job.FilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{job.FilePath}\"",
                    UseShellExecute = true
                });
                return;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(job.Folder) && Directory.Exists(job.Folder))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = job.Folder,
                UseShellExecute = true
            });
        }
    }

    private void RedownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        foreach (var job in selected)
        {
            RestartExistingJob(job);
        }
    }

    private void MoveFileContext_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Destination Folder to Move Files"
        };

        if (dialog.ShowDialog() == true)
        {
            var targetDir = dialog.FolderName;
            try
            {
                foreach (var job in selected)
                {
                    if (!string.IsNullOrEmpty(job.FilePath) && File.Exists(job.FilePath))
                    {
                        var fileName = Path.GetFileName(job.FilePath);
                        var newPath = Path.Combine(targetDir, fileName);
                        File.Move(job.FilePath, newPath, overwrite: true);
                        job.FilePath = newPath;
                        job.Folder = targetDir;
                    }
                    else if (!string.IsNullOrEmpty(job.Folder) && Directory.Exists(job.Folder))
                    {
                        var folderName = Path.GetFileName(job.Folder);
                        var newDir = Path.Combine(targetDir, folderName);
                        Directory.Move(job.Folder, newDir);
                        job.Folder = newDir;
                        job.FilePath = newDir;
                    }
                }
                MessageBox.Show($"Successfully moved {selected.Count} item(s) to:\n{targetDir}", "Quarry", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to move files: {ex.Message}", "Quarry Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RenameFileContext_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;
        var job = selected[0];

        var currentName = !string.IsNullOrEmpty(job.FilePath) ? Path.GetFileName(job.FilePath) : job.Title;
        var dialog = new RenameDialog(currentName, _settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
        {
            var newName = dialog.NewName.Trim();
            try
            {
                if (!string.IsNullOrEmpty(job.FilePath) && File.Exists(job.FilePath))
                {
                    var dir = Path.GetDirectoryName(job.FilePath)!;
                    var newPath = Path.Combine(dir, newName);
                    File.Move(job.FilePath, newPath);
                    job.FilePath = newPath;
                    job.Title = newName;
                }
                else
                {
                    job.Title = newName;
                }
                _jobsView.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to rename: {ex.Message}", "Quarry Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void DeleteFileDiskContext_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        var message = selected.Count == 1
            ? $"Are you sure you want to permanently delete this file from disk?\n\n{selected[0].Title}\n{selected[0].FilePath}"
            : $"Are you sure you want to permanently delete these {selected.Count} files from disk?";

        var res = MessageBox.Show(
            message,
            "Confirm File Deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res == MessageBoxResult.Yes)
        {
            foreach (var job in selected)
            {
                try
                {
                    if (!string.IsNullOrEmpty(job.FilePath) && File.Exists(job.FilePath))
                    {
                        File.Delete(job.FilePath);
                    }
                    else if (!string.IsNullOrEmpty(job.Folder) && Directory.Exists(job.Folder))
                    {
                        Directory.Delete(job.Folder, recursive: true);
                    }
                }
                catch { }

                if (_cancellations.TryGetValue(job.Id, out var cts))
                {
                    cts.Cancel();
                    _cancellations.Remove(job.Id);
                }
                _jobs.Remove(job);
            }
            UpdateCategoryBadges();
            _jobsView.Refresh();
        }
    }

    private void CopyFileToClipboardContext_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        var fileList = new System.Collections.Specialized.StringCollection();
        foreach (var job in selected)
        {
            if (!string.IsNullOrEmpty(job.FilePath) && File.Exists(job.FilePath))
            {
                fileList.Add(job.FilePath);
            }
            else if (!string.IsNullOrEmpty(job.Folder) && Directory.Exists(job.Folder))
            {
                fileList.Add(job.Folder);
            }
        }

        if (fileList.Count > 0)
        {
            try
            {
                Clipboard.SetFileDropList(fileList);
            }
            catch { }
        }
    }

    private void CopyPathContext_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        var paths = string.Join(Environment.NewLine, selected.Select(j => !string.IsNullOrEmpty(j.FilePath) ? j.FilePath : j.Folder));
        if (!string.IsNullOrEmpty(paths))
        {
            try
            {
                Clipboard.SetText(paths);
            }
            catch { }
        }
    }

    private void CopyUrlContext_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedJobs();
        if (selected.Count == 0) return;

        var urls = string.Join(Environment.NewLine, selected.Select(j => j.Url));
        if (!string.IsNullOrEmpty(urls))
        {
            try
            {
                Clipboard.SetText(urls);
            }
            catch { }
        }
    }

    private void PropertiesContext_Click(object sender, RoutedEventArgs e)
    {
        if (JobsDataGrid.SelectedItem is DownloadJob job)
        {
            var dialog = new FilePropertiesDialog(job, _settings)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
    }

    private void CategoryTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (CategoryTreeView.SelectedItem is TreeViewItem item && item.Tag is string tag)
        {
            _currentFilterTag = tag;
            _jobsView?.Refresh();
        }
    }

    #region View & Column Customization Handlers

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        bool show = ViewSidebarMenu.IsChecked;
        SidebarBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarColDef.Width = show ? new GridLength(175) : new GridLength(0);
    }

    private void CloseSidebar_Click(object sender, RoutedEventArgs e)
    {
        ViewSidebarMenu.IsChecked = false;
        SidebarBorder.Visibility = Visibility.Collapsed;
        SidebarColDef.Width = new GridLength(0);
    }

    private void ToggleToolbar_Click(object sender, RoutedEventArgs e)
    {
        ToolbarBorder.Visibility = ViewToolbarMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleStatusBar_Click(object sender, RoutedEventArgs e)
    {
        MainStatusBar.Visibility = ViewStatusBarMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private ColumnsVisibilityState GetCurrentColumnsVisibility()
    {
        return new ColumnsVisibilityState
        {
            ShowFileName = ColFileName.Visibility == Visibility.Visible,
            ShowSize = ColSize.Visibility == Visibility.Visible,
            ShowStatus = ColStatus.Visibility == Visibility.Visible,
            ShowProgress = ColProgress.Visibility == Visibility.Visible,
            ShowTransferRate = ColTransferRate.Visibility == Visibility.Visible,
            ShowTimeLeft = ColTimeLeft.Visibility == Visibility.Visible,
            ShowLastTryDate = ColLastTryDate.Visibility == Visibility.Visible,
            ShowUrl = ColUrl.Visibility == Visibility.Visible,
            ShowFolder = ColFolder.Visibility == Visibility.Visible,
            ShowCategory = ColCategory.Visibility == Visibility.Visible,
            ShowQuality = ColQuality.Visibility == Visibility.Visible
        };
    }

    private void ApplyColumnsVisibility(ColumnsVisibilityState state)
    {
        ColFileName.Visibility = state.ShowFileName ? Visibility.Visible : Visibility.Collapsed;
        ColSize.Visibility = state.ShowSize ? Visibility.Visible : Visibility.Collapsed;
        ColStatus.Visibility = state.ShowStatus ? Visibility.Visible : Visibility.Collapsed;
        ColProgress.Visibility = state.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        ColTransferRate.Visibility = state.ShowTransferRate ? Visibility.Visible : Visibility.Collapsed;
        ColTimeLeft.Visibility = state.ShowTimeLeft ? Visibility.Visible : Visibility.Collapsed;
        ColLastTryDate.Visibility = state.ShowLastTryDate ? Visibility.Visible : Visibility.Collapsed;
        ColUrl.Visibility = state.ShowUrl ? Visibility.Visible : Visibility.Collapsed;
        ColFolder.Visibility = state.ShowFolder ? Visibility.Visible : Visibility.Collapsed;
        ColCategory.Visibility = state.ShowCategory ? Visibility.Visible : Visibility.Collapsed;
        ColQuality.Visibility = state.ShowQuality ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HeaderContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu cm)
        {
            foreach (var item in cm.Items)
            {
                if (item is MenuItem mi && mi.Tag is string tag)
                {
                    switch (tag)
                    {
                        case "ColFileName": mi.IsChecked = ColFileName.Visibility == Visibility.Visible; break;
                        case "ColSize": mi.IsChecked = ColSize.Visibility == Visibility.Visible; break;
                        case "ColStatus": mi.IsChecked = ColStatus.Visibility == Visibility.Visible; break;
                        case "ColProgress": mi.IsChecked = ColProgress.Visibility == Visibility.Visible; break;
                        case "ColTransferRate": mi.IsChecked = ColTransferRate.Visibility == Visibility.Visible; break;
                        case "ColTimeLeft": mi.IsChecked = ColTimeLeft.Visibility == Visibility.Visible; break;
                        case "ColLastTryDate": mi.IsChecked = ColLastTryDate.Visibility == Visibility.Visible; break;
                        case "ColUrl": mi.IsChecked = ColUrl.Visibility == Visibility.Visible; break;
                        case "ColFolder": mi.IsChecked = ColFolder.Visibility == Visibility.Visible; break;
                        case "ColCategory": mi.IsChecked = ColCategory.Visibility == Visibility.Visible; break;
                        case "ColQuality": mi.IsChecked = ColQuality.Visibility == Visibility.Visible; break;
                    }
                }
            }
        }
    }

    private void HeaderColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string tag)
        {
            var isVis = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            switch (tag)
            {
                case "ColFileName": ColFileName.Visibility = isVis; break;
                case "ColSize": ColSize.Visibility = isVis; break;
                case "ColStatus": ColStatus.Visibility = isVis; break;
                case "ColProgress": ColProgress.Visibility = isVis; break;
                case "ColTransferRate": ColTransferRate.Visibility = isVis; break;
                case "ColTimeLeft": ColTimeLeft.Visibility = isVis; break;
                case "ColLastTryDate": ColLastTryDate.Visibility = isVis; break;
                case "ColUrl": ColUrl.Visibility = isVis; break;
                case "ColFolder": ColFolder.Visibility = isVis; break;
                case "ColCategory": ColCategory.Visibility = isVis; break;
                case "ColQuality": ColQuality.Visibility = isVis; break;
            }
        }
    }

    private void ResetColumnsMenu_Click(object sender, RoutedEventArgs e)
    {
        ApplyColumnsVisibility(new ColumnsVisibilityState());
    }

    private void CustomizeColumns_Click(object sender, RoutedEventArgs e)
    {
        var current = GetCurrentColumnsVisibility();
        var dlg = new CustomizeColumnsDialog(current, _settings)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
        {
            ApplyColumnsVisibility(dlg.State);
        }
    }

    #endregion
}