using System.Collections.ObjectModel;
using System.Windows.Threading;
using SwiftGet.App.Infrastructure;
using SwiftGet.App.Themes;
using SwiftGet.Core.Downloads;

namespace SwiftGet.App.ViewModels;

/// <summary>
/// Satu baris unduhan.
/// <para>
/// Baris punya dua bentuk sesuai `DESIGN.md` bagian 7: mode penuh saat aktif
/// (menampilkan kecepatan dan sisa waktu) dan mode ringkas saat selesai atau
/// dijeda (kecepatan nol bukan informasi yang layak menempati ruang).
/// </para>
/// </summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private const int SpeedHistoryLength = 60;

    private DownloadState _state;
    private double _fraction;
    private long _bytesDownloaded;
    private long? _totalBytes;
    private double _bytesPerSecond;
    private int _completedSegments;
    private int _totalSegments;
    private TimeSpan? _eta;
    private string? _error;
    private bool _isSelected;

    public DownloadItemViewModel(DownloadTask task)
    {
        Task = task;
        FileName = task.FileName;
        Url = task.Url.ToString();
        _state = task.State;
        _totalBytes = task.TotalLength;
        SpeedHistory = new ObservableCollection<double>();
        Segments = new ObservableCollection<SegmentView>();
    }

    public DownloadTask Task { get; }

    public string Id => Task.Id;

    public string FileName { get; private set; }

    public string Url { get; }

    /// <summary>Kategori berkas dari ekstensi namanya; berubah bila server
    /// memberi nama akhir lewat Content-Disposition.</summary>
    public string CategoryText => FileCategories.LabelOf(FileName);

    public string Host
    {
        get
        {
            try { return Task.Url.Host; }
            catch (UriFormatException) { return ""; }
        }
    }

    /// <summary>Riwayat kecepatan unduhan ini, untuk sparkline di panel periksa.</summary>
    public ObservableCollection<double> SpeedHistory { get; }

    /// <summary>Keadaan tiap segmen, untuk peta segmen di panel periksa.</summary>
    public ObservableCollection<SegmentView> Segments { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
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
            OnPropertyChanged(nameof(CanOpenFile));
            OnPropertyChanged(nameof(IsCompact));
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(HasError));
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
        _ => "Tidak diketahui",
    };

    /// <summary>Selesai dan dijeda tidak perlu baris tinggi: datanya statis.</summary>
    public bool IsCompact => _state is DownloadState.Completed or DownloadState.Paused or DownloadState.Canceled;

    public bool IsExpanded => !IsCompact;

    public double Fraction
    {
        get => _fraction;
        private set
        {
            if (!SetProperty(ref _fraction, value)) return;
            OnPropertyChanged(nameof(PercentText));
            OnPropertyChanged(nameof(ProgressPercent));
        }
    }

    public string PercentText => Humanize.Percent(_fraction);

    /// <summary>Nilai 0 sampai 100 untuk bilah progres.</summary>
    public double ProgressPercent => _fraction * 100;

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
            OnPropertyChanged(nameof(TotalSizeText));
        }
    }

    public string SizeText => TotalBytes is > 0
        ? $"{Humanize.Bytes(BytesDownloaded)} / {Humanize.Bytes(TotalBytes.Value)}"
        : Humanize.Bytes(BytesDownloaded);

    public string TotalSizeText => TotalBytes is > 0 ? Humanize.Bytes(TotalBytes.Value) : "-";

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        private set
        {
            if (!SetProperty(ref _bytesPerSecond, value)) return;
            OnPropertyChanged(nameof(SpeedText));
        }
    }

    public string SpeedText => Humanize.BytesPerSecond(_bytesPerSecond);

    public TimeSpan? Eta
    {
        get => _eta;
        private set
        {
            if (!SetProperty(ref _eta, value)) return;
            OnPropertyChanged(nameof(EtaText));
        }
    }

    public string EtaText => _eta is null ? "-" : Humanize.Duration(_eta) + " tersisa";

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
            OnPropertyChanged(nameof(ConnectionText));
        }
    }

    public string SegmentText => TotalSegments > 0
        ? $"{CompletedSegments}/{TotalSegments} bagian"
        : "-";

    public string ConnectionText => TotalSegments > 0
        ? $"{TotalSegments} koneksi"
        : "-";

    public string? Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _state == DownloadState.Failed && !string.IsNullOrWhiteSpace(_error);

    public string RangeSupportText => Task.SupportsRanges ? "Didukung (206)" : "Tidak didukung";

    public string TargetFolder => Task.TargetDirectory;

    public string ValidatorText => string.IsNullOrWhiteSpace(Task.ETag) ? "-" : Task.ETag!;

    public bool CanPause => _state is DownloadState.Downloading or DownloadState.Probing or DownloadState.Queued;

    public bool CanResume => _state.IsResumable();

    public bool CanCancel => _state is not (DownloadState.Completed or DownloadState.Canceled);

    public bool CanOpenFile => _state == DownloadState.Completed;

    /// <summary>Terapkan potret progres dari engine.</summary>
    public void Apply(DownloadProgress progress)
    {
        State = progress.State;

        // Unduhan media (HLS/DASH) tidak tahu ukuran totalnya; kemajuan yang
        // jujur adalah rasio segmen, bukan persen karangan dari byte.
        double fraction = progress.Fraction;
        if (progress.TotalBytes is null && progress.TotalSegments > 0)
            fraction = (double)progress.CompletedSegments / progress.TotalSegments;
        Fraction = fraction;

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

        RefreshSegments();
        RecordSpeed(progress.BytesPerSecond);
    }

    /// <summary>Perbarui status tanpa potret progres penuh.</summary>
    public void ApplyState(DownloadState state, string? error)
    {
        State = state;
        if (state == DownloadState.Failed)
        {
            Error = error;
        }

        if (state is DownloadState.Paused or DownloadState.Completed)
        {
            // Kecepatan nol supaya ticker tidak menampilkan angka basi.
            BytesPerSecond = 0;
            Eta = null;
        }

        RefreshSegments();
        OnPropertyChanged(nameof(ValidatorText));
        OnPropertyChanged(nameof(RangeSupportText));
        OnPropertyChanged(nameof(TargetFolder));
    }

    /// <summary>Segarkan identitas yang bisa berubah setelah probe selesai.</summary>
    public void RefreshFromTask()
    {
        FileName = Task.FileName;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(CategoryText));
        OnPropertyChanged(nameof(ValidatorText));
        OnPropertyChanged(nameof(RangeSupportText));
        OnPropertyChanged(nameof(TargetFolder));

        IReadOnlyList<Segment> segments = Task.GetSegments();
        CompletedSegments = segments.Count(s => s.IsComplete);
        TotalSegments = segments.Count;
        RefreshSegments();
    }

    private void RefreshSegments()
    {
        IReadOnlyList<Segment> source = Task.GetSegments();

        // Koleksi dimutasi di tempat agar animasi batang tidak dipicu ulang
        // setiap 500 ms; hanya nilai yang berubah yang diganti.
        if (Segments.Count != source.Count)
        {
            Segments.Clear();
            foreach (Segment segment in source)
            {
                Segments.Add(new SegmentView(
                    segment.Index,
                    segment.Length == 0 ? 0 : (double)segment.BytesDone / segment.Length,
                    segment.IsComplete));
            }
            return;
        }

        for (int i = 0; i < source.Count; i++)
        {
            Segment segment = source[i];
            double fraction = segment.Length == 0 ? 0 : (double)segment.BytesDone / segment.Length;
            SegmentView current = Segments[i];

            if (Math.Abs(current.Fraction - fraction) > 0.0001 || current.IsComplete != segment.IsComplete)
            {
                Segments[i] = new SegmentView(segment.Index, fraction, segment.IsComplete);
            }
        }
    }

    private void RecordSpeed(double speed)
    {
        SpeedHistory.Add(speed);
        while (SpeedHistory.Count > SpeedHistoryLength) SpeedHistory.RemoveAt(0);
    }
}

/// <summary>
/// Daftar unduhan beserta ringkasan agregat.
/// </summary>
public sealed class DownloadsViewModel : ObservableObject, IDisposable
{
    private const int SpeedHistoryLength = 60;

    private readonly DownloadManager _manager;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _sampler;
    private readonly Queue<double> _speedHistory = new();

    private double _totalSpeed;
    private int _activeCount;
    private string _summaryText = "Belum ada unduhan";
    private bool _disposed;

    public DownloadsViewModel(DownloadManager manager, Dispatcher dispatcher)
    {
        _manager = manager;
        _dispatcher = dispatcher;

        Items = new ObservableCollection<DownloadItemViewModel>();
        VisibleItems = new ObservableCollection<DownloadItemViewModel>();
        SpeedHistory = new ObservableCollection<double>();

        _manager.ProgressChanged += OnProgressChanged;
        _manager.StateChanged += OnStateChanged;

        _sampler = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _sampler.Tick += (_, _) => SampleTotals();
        _sampler.Start();
    }

    /// <summary>Semua unduhan, tanpa penyaring.</summary>
    public ObservableCollection<DownloadItemViewModel> Items { get; }

    /// <summary>
    /// Unduhan yang tampil setelah filter dan pengurutan. Koleksi inilah yang
    /// diikat ke daftar di tampilan, sehingga menyaring tidak mengacaukan
    /// urutan sisipan di <see cref="Items"/>.
    /// </summary>
    public ObservableCollection<DownloadItemViewModel> VisibleItems { get; }

    private DownloadFilter _filter = DownloadFilter.All;
    private DownloadSort _sort = DownloadSort.Newest;

    /// <summary>Penyaring aktif. Mengubahnya langsung memperbarui daftar.</summary>
    public DownloadFilter Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            OnPropertyChanged(nameof(FilterLabel));
            OnPropertyChanged(nameof(HasFilter));
            ApplyFilterAndSort();
        }
    }

    public string FilterLabel => _filter switch
    {
        DownloadFilter.All => "Semua",
        DownloadFilter.Active => "Berjalan",
        DownloadFilter.Failed => "Gagal",
        DownloadFilter.Completed => "Selesai",
        _ => "Semua",
    };

    /// <summary>True bila penyaring bukan "semua".</summary>
    public bool HasFilter => _filter != DownloadFilter.All;

    public DownloadSort Sort
    {
        get => _sort;
        set
        {
            if (!SetProperty(ref _sort, value)) return;
            OnPropertyChanged(nameof(SortLabel));
            ApplyFilterAndSort();
        }
    }

    public string SortLabel => _sort switch
    {
        DownloadSort.Newest => "Terbaru",
        DownloadSort.Status => "Status",
        DownloadSort.Size => "Ukuran",
        _ => "Terbaru",
    };

    /// <summary>Pilihan dropdown kategori; <see cref="Value"/> null berarti tanpa penyaring.</summary>
    public sealed record CategoryOption(string Label, DownloadCategory? Value)
    {
        public override string ToString() => Label;
    }

    private static readonly List<CategoryOption> CategoryOptionList = new()
    {
        new("Semua kategori", null),
        new(FileCategories.Label(DownloadCategory.Video), DownloadCategory.Video),
        new(FileCategories.Label(DownloadCategory.Audio), DownloadCategory.Audio),
        new(FileCategories.Label(DownloadCategory.Gambar), DownloadCategory.Gambar),
        new(FileCategories.Label(DownloadCategory.Dokumen), DownloadCategory.Dokumen),
        new(FileCategories.Label(DownloadCategory.Arsip), DownloadCategory.Arsip),
        new(FileCategories.Label(DownloadCategory.Program), DownloadCategory.Program),
        new(FileCategories.Label(DownloadCategory.Lainnya), DownloadCategory.Lainnya),
    };

    /// <summary>Isi dropdown kategori; statis karena taksonomi tidak berubah saat berjalan.</summary>
    public IReadOnlyList<CategoryOption> CategoryOptions => CategoryOptionList;

    private CategoryOption _selectedCategoryOption = CategoryOptionList[0];

    public CategoryOption SelectedCategoryOption
    {
        get => _selectedCategoryOption;
        set
        {
            if (!SetProperty(ref _selectedCategoryOption, value)) return;
            ApplyFilterAndSort();
        }
    }

    /// <summary>Kategori aktif dari pilihan dropdown; null berarti semua.</summary>
    private DownloadCategory? SelectedCategory => _selectedCategoryOption.Value;

    /// <summary>Riwayat kecepatan total, untuk sparkline header.</summary>
    public ObservableCollection<double> SpeedHistory { get; }

    public double TotalSpeed
    {
        get => _totalSpeed;
        private set
        {
            if (!SetProperty(ref _totalSpeed, value)) return;
            OnPropertyChanged(nameof(TotalSpeedText));
        }
    }

    public string TotalSpeedText => Humanize.BytesPerSecond(TotalSpeed);

    public int ActiveCount
    {
        get => _activeCount;
        private set
        {
            if (!SetProperty(ref _activeCount, value)) return;
            UpdateSummary();
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>True bila daftar kosong, dipakai keadaan kosong di UI.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>True bila ada unduhan, tapi penyaring menyembunyikan semuanya.</summary>
    public bool IsFilteredEmpty => Items.Count > 0 && VisibleItems.Count == 0;

    public DownloadItemViewModel Add(DownloadTask task)
    {
        var vm = new DownloadItemViewModel(task);
        Items.Insert(0, vm);
        ApplyFilterAndSort();
        UpdateSummary();
        return vm;
    }

    public void Reload(IEnumerable<DownloadTask> tasks)
    {
        Items.Clear();
        foreach (DownloadTask task in tasks)
            Items.Add(new DownloadItemViewModel(task));
        ApplyFilterAndSort();
        UpdateSummary();
    }

    public void Remove(string id)
    {
        DownloadItemViewModel? vm = Items.FirstOrDefault(i => i.Id == id);
        if (vm is null) return;
        Items.Remove(vm);
        ApplyFilterAndSort();
        UpdateSummary();
    }

    /// <summary>
    /// Terapkan penyaring dan pengurutan ke <see cref="VisibleItems"/> dengan
    /// rekonsiliasi in-place (Remove/Insert/Move), bukan Clear dan isi ulang.
    /// <para>
    /// Clear memicu Reset dan melempar seluruh seleksi ListBox — panel periksa
    /// jadi tampak kosong setiap daftar disaring ulang, yang terjadi berkali-
    /// kali per detik selama unduhan berjalan. Dengan perubahan parsial,
    /// seleksi dan container baris ikut bertahan. Daftar unduhan jarang lebih
    /// dari beberapa puluh baris, jadi pencarian linier di sini murah.
    /// </para>
    /// </summary>
    public void ApplyFilterAndSort()
    {
        IEnumerable<DownloadItemViewModel> query = Items;

        query = _filter switch
        {
            DownloadFilter.Active => query.Where(i =>
                i.State is DownloadState.Downloading or DownloadState.Probing
                    or DownloadState.Queued or DownloadState.Paused),
            DownloadFilter.Failed => query.Where(i => i.State == DownloadState.Failed),
            DownloadFilter.Completed => query.Where(i => i.State == DownloadState.Completed),
            _ => query,
        };

        // Kategori menindak status: baris harus lolos keduanya.
        if (SelectedCategory is DownloadCategory category)
            query = query.Where(i => FileCategories.Classify(i.FileName) == category);

        query = _sort switch
        {
            DownloadSort.Status => query.OrderBy(SortKeyForStatus).ThenBy(i => i.FileName, StringComparer.CurrentCulture),
            DownloadSort.Size => query.OrderByDescending(i => i.TotalBytes ?? 0).ThenBy(i => i.FileName, StringComparer.CurrentCulture),
            _ => query,
        };

        List<DownloadItemViewModel> desired = query.ToList();

        for (int i = VisibleItems.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(VisibleItems[i]))
                VisibleItems.RemoveAt(i);
        }

        for (int target = 0; target < desired.Count; target++)
        {
            int current = VisibleItems.IndexOf(desired[target]);
            if (current < 0)
                VisibleItems.Insert(target, desired[target]);
            else if (current != target)
                VisibleItems.Move(current, target);
        }
    }

    /// <summary>
    /// Urutan status: yang gagal lebih dulu karena butuh tindakan, lalu yang
    /// berjalan, dijeda, dan selesai di akhir.
    /// </summary>
    private static int SortKeyForStatus(DownloadItemViewModel item) => item.State switch
    {
        DownloadState.Failed => 0,
        DownloadState.Downloading or DownloadState.Probing => 1,
        DownloadState.Queued => 2,
        DownloadState.Paused => 3,
        DownloadState.Completed => 4,
        DownloadState.Canceled => 5,
        _ => 6,
    };

    private void UpdateSummary()
    {
        int total = Items.Count;

        if (total == 0)
        {
            SummaryText = "Belum ada unduhan";
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsFilteredEmpty));
            return;
        }

        int done = Items.Count(i => i.State == DownloadState.Completed);
        SummaryText = ActiveCount > 0
            ? $"{done} selesai, {ActiveCount} berjalan, {total} total"
            : $"{done} selesai dari {total} unduhan";

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
    }

    private void OnProgressChanged(DownloadProgress progress)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnProgressChanged(progress)));
            return;
        }

        DownloadItemViewModel? item = Items.FirstOrDefault(i => i.Id == progress.Id);
        if (item is null) return;

        if (item.TotalSegments != progress.TotalSegments) item.RefreshFromTask();
        item.Apply(progress);

        // Status bisa memindahkan item antar penyaring (misal "Gagal" ke
        // "Selesai"), jadi daftar harus disaring ulang.
        ApplyFilterAndSort();
        UpdateSummary();
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
        ApplyFilterAndSort();
        UpdateSummary();
    }

    private void SampleTotals()
    {
        double total = 0;
        int active = 0;

        foreach (DownloadItemViewModel item in Items)
        {
            if (item.State != DownloadState.Downloading) continue;
            total += item.BytesPerSecond;
            active++;
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
    }

    /// <summary>Hentikan sampler saat jendela ditutup, tanpa membuang data.</summary>
    public void StopSampling() => _sampler.Stop();
}

