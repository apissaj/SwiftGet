using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using SwiftGet.App.Infrastructure;
using SwiftGet.Core;
using SwiftGet.Core.Downloads;

namespace SwiftGet.App.ViewModels;

/// <summary>
/// Satu baris unduhan di daftar. Semua nilai yang ditampilkan sudah diformat
/// supaya XAML tidak perlu konverter.
/// </summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private DownloadState _state;
    private double _fraction;
    private long _bytesDownloaded;
    private long? _totalBytes;
    private double _bytesPerSecond;
    private int _completedSegments;
    private int _totalSegments;
    private TimeSpan? _eta;
    private string? _error;

    public DownloadItemViewModel(DownloadTask task)
    {
        Task = task;
        FileName = task.FileName;
        Url = task.Url.ToString();
        SegmentCount = 0;
        _state = task.State;
    }

    public DownloadTask Task { get; }

    public string Id => Task.Id;

    public string FileName { get; private set; }

    public string Url { get; }

    public string WebHost
    {
        get
        {
            try { return Task.Url.Host; }
            catch { return ""; }
        }
    }

    public DownloadState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public string StateText => _state switch
    {
        DownloadState.Queued => "Menunggu",
        DownloadState.Probing => "Memeriksa",
        DownloadState.Downloading => "Mengunduh",
        DownloadState.Paused => "Dijeda",
        DownloadState.Completed => "Selesai",
        DownloadState.Failed => "Gagal",
        DownloadState.Canceled => "Dibatalkan",
        _ => "?",
    };

    public double Fraction
    {
        get => _fraction;
        private set
        {
            if (!SetProperty(ref _fraction, value)) return;
            OnPropertyChanged(nameof(PercentText));
        }
    }

    public string PercentText => Humanize.Percent(_fraction);

    public long BytesDownloaded
    {
        get => _bytesDownloaded;
        private set
        {
            if (!SetProperty(ref _bytesDownloaded, value)) return;
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        private set
        {
            if (!SetProperty(ref _totalBytes, value)) return;
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public string SizeText => TotalBytes is > 0
        ? $"{Humanize.Bytes(BytesDownloaded)} / {Humanize.Bytes(TotalBytes.Value)}"
        : Humanize.Bytes(BytesDownloaded);

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        private set
        {
            if (!SetProperty(ref _bytesPerSecond, value)) return;
            OnPropertyChanged(nameof(SpeedText));
        }
    }

    public string SpeedText => _state == DownloadState.Downloading
        ? Humanize.BytesPerSecond(_bytesPerSecond)
        : "-";

    public TimeSpan? Eta
    {
        get => _eta;
        private set
        {
            if (!SetProperty(ref _eta, value)) return;
            OnPropertyChanged(nameof(EtaText));
        }
    }

    public string EtaText => _state == DownloadState.Downloading ? Humanize.Duration(_eta) : "-";

    public int CompletedSegments
    {
        get => _completedSegments;
        private set
        {
            if (!SetProperty(ref _completedSegments, value)) return;
            OnPropertyChanged(nameof(SegmentText));
        }
    }

    public int TotalSegments
    {
        get => _totalSegments;
        private set
        {
            if (!SetProperty(ref _totalSegments, value)) return;
            OnPropertyChanged(nameof(SegmentText));
        }
    }

    private int SegmentCount { get; set; }

    public string SegmentText => TotalSegments > 0
        ? $"{CompletedSegments}/{TotalSegments} bagian"
        : SegmentCount > 0 ? $"{SegmentCount} bagian" : "-";

    public string? Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public bool IsActive => _state.IsActive();

    public bool CanPause => _state is DownloadState.Downloading or DownloadState.Probing or DownloadState.Queued;

    public bool CanResume => _state.IsResumable();

    public bool CanCancel => _state is not (DownloadState.Completed or DownloadState.Canceled);

    /// <summary>Terapkan snapshot progres dari engine ke properti tampilan.</summary>
    public void Apply(DownloadProgress progress)
    {
        State = progress.State;
        Fraction = progress.Fraction;
        BytesDownloaded = progress.BytesDownloaded;
        TotalBytes = progress.TotalBytes;
        BytesPerSecond = progress.BytesPerSecond;
        CompletedSegments = progress.CompletedSegments;
        TotalSegments = progress.TotalSegments;
        Eta = progress.Eta;

        if (progress.State == DownloadState.Failed)
        {
            Error = Task.LastError;
        }
        else if (progress.State == DownloadState.Completed)
        {
            Error = null;
        }

        OnPropertyChanged(nameof(SegmentText));
    }

    /// <summary>Perbarui status tanpa snapshot lengkap (mis. saat antre).</summary>
    public void ApplyState(DownloadState state, string? error)
    {
        State = state;
        if (state == DownloadState.Failed) Error = error;
        OnPropertyChanged(nameof(SegmentText));
    }

    /// <summary>Segarkan info segmen dari task (dipakai setelah probe selesai).</summary>
    public void RefreshFromTask()
    {
        FileName = Task.FileName;
        OnPropertyChanged(nameof(FileName));

        var segments = Task.GetSegments();
        CompletedSegments = segments.Count(s => s.IsComplete);
        // Set TotalSegments terakhir: ia memicu OnPropertyChanged(SegmentText).
        TotalSegments = segments.Count;
        SegmentCount = segments.Count;
    }
}

/// <summary>
/// Daftar unduhan beserta ringkasan agregat (total kecepatan, jumlah aktif).
/// </summary>
public sealed class DownloadsViewModel : ObservableObject, IDisposable
{
    private const int SpeedHistoryLength = 60;

    private readonly DownloadManager _manager;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly DispatcherTimer _sampler;


    private double _totalSpeed;
    private int _activeCount;
    private string _totalSpeedText = "-";
    private bool _disposed;

    public DownloadsViewModel(DownloadManager manager, Dispatcher dispatcher)
    {
        _manager = manager;
        _dispatcher = dispatcher;

        Items = new ObservableCollection<DownloadItemViewModel>();
        Items.CollectionChanged += OnItemsChanged;

        _manager.ProgressChanged += OnProgressChanged;
        _manager.StateChanged += OnStateChanged;

        _sampler = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _sampler.Tick += (_, _) => SampleTotals();
        _sampler.Start();

        SpeedHistory = new ObservableCollection<double>();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; }

    /// <summary>Riwayat kecepatan total (byte/detik) untuk grafik.</summary>
    public ObservableCollection<double> SpeedHistory { get; }

    public double TotalSpeed
    {
        get => _totalSpeed;
        private set
        {
            if (!SetProperty(ref _totalSpeed, value)) return;
            TotalSpeedText = Humanize.BytesPerSecond(value);
        }
    }

    public string TotalSpeedText
    {
        get => _totalSpeedText;
        private set => SetProperty(ref _totalSpeedText, value);
    }

    public int ActiveCount
    {
        get => _activeCount;
        private set
        {
            if (!SetProperty(ref _activeCount, value)) return;
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public string SummaryText
    {
        get
        {
            int total = Items.Count;
            int done = Items.Count(i => i.State == DownloadState.Completed);
            return $"{total} unduhan - {done} selesai - {ActiveCount} aktif";
        }
    }

    /// <summary>Tambah item baru ke daftar (dipanggil di thread UI).</summary>
    public DownloadItemViewModel Add(DownloadTask task)
    {
        var vm = new DownloadItemViewModel(task);
        Items.Insert(0, vm);
        return vm;
    }

    /// <summary>Muat ulang seluruh unduhan tersimpan ke daftar.</summary>
    public void Reload(IEnumerable<DownloadTask> tasks)
    {
        Items.Clear();
        foreach (DownloadTask task in tasks)
            Items.Add(new DownloadItemViewModel(task));
    }

    public void Remove(string id)
    {
        DownloadItemViewModel? vm = Items.FirstOrDefault(i => i.Id == id);
        if (vm is not null) Items.Remove(vm);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(SummaryText));

    private void OnProgressChanged(DownloadProgress progress)
    {
        // Event berasal dari thread worker; marshalkan ke UI.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnProgressChanged(progress)));
            return;
        }

        DownloadItemViewModel? item = Items.FirstOrDefault(i => i.Id == progress.Id);
        if (item is null) return;

        if (item.TotalSegments != progress.TotalSegments) item.RefreshFromTask();
        item.Apply(progress);
        OnPropertyChanged(nameof(SummaryText));
    }

    private void OnStateChanged(string id, DownloadState state, string? error)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnStateChanged(id, state, error)));
            return;
        }

        DownloadItemViewModel? item = Items.FirstOrDefault(i => i.Id == id);
        item?.ApplyState(state, error);
        OnPropertyChanged(nameof(SummaryText));
    }

    private void SampleTotals()
    {
        double total = 0;
        int active = 0;

        foreach (DownloadItemViewModel item in Items)
        {
            if (item.State == DownloadState.Downloading)
            {
                total += item.BytesPerSecond;
                active++;
            }
        }

        TotalSpeed = total;
        ActiveCount = active;

        SpeedHistory.Add(total);
        while (SpeedHistory.Count > SpeedHistoryLength) SpeedHistory.RemoveAt(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sampler.Stop();
        _manager.ProgressChanged -= OnProgressChanged;
        _manager.StateChanged -= OnStateChanged;
        Items.CollectionChanged -= OnItemsChanged;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}




