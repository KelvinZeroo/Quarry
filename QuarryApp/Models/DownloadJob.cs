using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QuarryApp.Models;

public enum JobState
{
    Queued,
    Running,
    Asking,
    Done,
    Error,
    Stopped
}

public class DownloadJob : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N")[..10];
    private string _url = string.Empty;
    private string _title = string.Empty;
    private string _folder = string.Empty;
    private JobState _state = JobState.Queued;
    private int _total;
    private int _downloaded;
    private int _skipped;
    private int _failed;
    private long? _totalBytes;
    private long _downloadedBytes;
    private int _overridePercentage = -1;
    private string _transferRate = "—";
    private string _timeLeft = "—";
    private string _error = string.Empty;
    private DateTime _dateAdded = DateTime.Now;
    private VideoStreamQuality? _selectedQuality;

    public VideoStreamQuality? SelectedQuality
    {
        get => _selectedQuality;
        set => SetField(ref _selectedQuality, value);
    }

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Url
    {
        get => _url;
        set => SetField(ref _url, value);
    }

    public string Title
    {
        get => string.IsNullOrWhiteSpace(_title) ? _url : _title;
        set => SetField(ref _title, value);
    }

    private string _filePath = string.Empty;

    public string FilePath
    {
        get => _filePath;
        set => SetField(ref _filePath, value);
    }

    public string Folder
    {
        get => _folder;
        set => SetField(ref _folder, value);
    }

    public JobState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public int Total
    {
        get => _total;
        set
        {
            if (SetField(ref _total, value))
            {
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(ProgressDisplay));
                OnPropertyChanged(nameof(SizeDisplay));
            }
        }
    }

    public int Downloaded
    {
        get => _downloaded;
        set
        {
            if (SetField(ref _downloaded, value))
            {
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public int Skipped
    {
        get => _skipped;
        set
        {
            if (SetField(ref _skipped, value))
            {
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public int Failed
    {
        get => _failed;
        set => SetField(ref _failed, value);
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetField(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(SizeDisplay));
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (SetField(ref _downloadedBytes, value))
            {
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public void SetPercentage(int pct)
    {
        _overridePercentage = pct;
        OnPropertyChanged(nameof(ProgressPercentage));
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    public void IncrementDownloaded()
    {
        Interlocked.Increment(ref _downloaded);
        OnPropertyChanged(nameof(Downloaded));
        OnPropertyChanged(nameof(ProgressPercentage));
        OnPropertyChanged(nameof(ProgressDisplay));
    }

    public void IncrementSkipped()
    {
        Interlocked.Increment(ref _skipped);
        OnPropertyChanged(nameof(Skipped));
        OnPropertyChanged(nameof(ProgressPercentage));
        OnPropertyChanged(nameof(ProgressDisplay));
    }

    public void IncrementFailed()
    {
        Interlocked.Increment(ref _failed);
        OnPropertyChanged(nameof(Failed));
    }

    public string TransferRate
    {
        get => _transferRate;
        set => SetField(ref _transferRate, value);
    }

    public string TimeLeft
    {
        get => _timeLeft;
        set => SetField(ref _timeLeft, value);
    }

    public string Error
    {
        get => _error;
        set => SetField(ref _error, value);
    }

    public DateTime DateAdded
    {
        get => _dateAdded;
        set => SetField(ref _dateAdded, value);
    }

    public int ProgressPercentage
    {
        get
        {
            if (_overridePercentage >= 0) return _overridePercentage;
            if (TotalBytes > 0)
            {
                return Math.Clamp((int)Math.Round((double)DownloadedBytes / TotalBytes.Value * 100), 0, 100);
            }
            if (Total <= 0) return 0;
            var processed = Downloaded + Skipped + Failed;
            return Math.Clamp((int)Math.Round((double)processed / Total * 100), 0, 100);
        }
    }

    public string ProgressDisplay => $"{ProgressPercentage}%";

    public string SizeDisplay
    {
        get
        {
            if (TotalBytes.HasValue && TotalBytes.Value > 0)
            {
                var bytes = TotalBytes.Value;
                if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
                if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
                return $"{bytes / 1024.0:F0} KB";
            }
            if (Total > 1) return $"{Total} items";
            return "—";
        }
    }

    public string StatusDisplay => State switch
    {
        JobState.Done => "Complete",
        JobState.Running => $"{ProgressPercentage}%",
        JobState.Asking => "Decision Needed",
        JobState.Error => "Error",
        JobState.Stopped => "Stopped",
        _ => "Queued"
    };

    public List<string> LogLines { get; } = new();

    public event Action<string>? LogMessageAdded;

    public void AddLog(string msg)
    {
        lock (LogLines)
        {
            LogLines.Add(msg);
        }
        try
        {
            LogMessageAdded?.Invoke(msg);
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
