using System.Collections.ObjectModel;
using System.Windows.Threading;
using SwiftGet.App.Infrastructure;
using SwiftGet.Core;
using SwiftGet.Core.Downloads;
using SwiftGet.Core.Scheduling;

namespace SwiftGet.App.ViewModels;

/// <summary>
/// View model jendela utama: daftar unduhan, panel periksa, dan status aplikasi.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly HistoryStore _history = new();
    private readonly ExclusionList _exclusions;
    private readonly CancellationTokenSource _cts = new();

    private string _newUrl = "";
    private string _statusMessage = "Siap";
    private bool _isBusy;
    private bool _isDark;
    private DownloadItemViewModel? _selectedItem;
    private bool _isInspectorOpen;
    private bool _disposed;

    public MainViewModel(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;

        var options = new SchedulerOptions
        {
            MaxConcurrentDownloads = settings.MaxConcurrentDownloads,
            MaxConnectionsPerDownload = settings.MaxConnectionsPerDownload,
            MaxConnectionsPerHost = settings.MaxConnectionsPerHost,
            FfmpegPath = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? null : settings.FfmpegPath,
            CategoryFolders = settings.MoveToCategoryFolders,
        };

        ExclusionList.WriteSeed(Path.Combine(AppSettings.StateDirectory, "exclusions.txt"));
        _exclusions = new ExclusionList(Path.Combine(AppSettings.StateDirectory, "exclusions.txt"));

        Manager = new DownloadManager(AppSettings.StateDirectory, options);
        Manager.SetSpeedLimit((long)settings.SpeedLimitKbPerSecond * 1024);
        Downloads = new DownloadsViewModel(Manager, dispatcher);

        AddCommand = new AsyncRelayCommand(AddAndStartAsync, () => !IsBusy && HasValidUrl);
        AddToQueueCommand = new RelayCommand(_ => AddOnly(), new Func<object?, bool>(_ => HasValidUrl));
        PauseCommand = new RelayCommand(_ => PauseSelected(), _ => SelectedItem?.CanPause == true);
        ResumeCommand = new AsyncRelayCommand(_ => ResumeSelectedAsync(), _ => SelectedItem?.CanResume == true);
        CancelCommand = new RelayCommand(_ => CancelSelected(), _ => SelectedItem?.CanCancel == true);
        OpenFolderCommand = new RelayCommand(_ => OpenDownloadFolder(), new Func<object?, bool>(_ => Directory.Exists(DownloadDir)));
        OpenFileCommand = new RelayCommand(_ => OpenSelectedLocation(), _ => SelectedItem is not null);
        PauseAllCommand = new RelayCommand(_ => PauseAll(), new Func<object?, bool>(_ => Downloads.Items.Any(i => i.CanPause)));
        ResumeAllCommand = new AsyncRelayCommand(ResumeAllAsync, () => Downloads.Items.Any(i => i.CanResume));
        ClearCompletedCommand = new RelayCommand(_ => ClearCompleted(), new Func<object?, bool>(_ => Downloads.Items.Any(i => i.State == DownloadState.Completed)));
        CopyUrlCommand = new RelayCommand(_ => CopySelectedUrl(), _ => SelectedItem is not null);
        CloseInspectorCommand = new RelayCommand(_ => IsInspectorOpen = false);
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        FocusUrlCommand = new RelayCommand(_ => FocusUrlRequested?.Invoke(this, EventArgs.Empty));

        SetFilterCommand = new RelayCommand(param =>
        {
            // CommandParameter dari XAML selalu string, jadi penguraian enum
            // dilakukan di sini; tidak perlu converter di antaranya.
            if (Enum.TryParse(param?.ToString(), ignoreCase: true, out DownloadFilter filter))
                Downloads.Filter = filter;
        });
        ClearFilterCommand = new RelayCommand(_ => Downloads.Filter = DownloadFilter.All,
            new Func<object?, bool>(_ => Downloads.HasFilter));

        Downloads.Items.CollectionChanged += (_, _) => RefreshCommandStates();
    }

    public DownloadManager Manager { get; }

    public DownloadsViewModel Downloads { get; }

    /// <summary>Riwayat unduhan selesai; dibuka lewat tombol Riwayat.</summary>
    public HistoryStore History => _history;

    public string DownloadDir => _settings.DownloadDirectory;

    /// <summary>Diminta UI untuk memindahkan fokus ke kotak URL.</summary>
    public event EventHandler? FocusUrlRequested;

    /// <summary>Diminta UI saat unduhan selesai (untuk notifikasi tray).</summary>
    public event Action<DownloadItemViewModel>? DownloadCompleted;

    public string NewUrl
    {
        get => _newUrl;
        set
        {
            if (!SetProperty(ref _newUrl, value)) return;
            OnPropertyChanged(nameof(HasValidUrl));
            RefreshCommandStates();
        }
    }

    /// <summary>True bila isi kotak URL bisa dipakai. Dipakai juga untuk keadaan tombol.</summary>
    public bool HasValidUrl =>
        !string.IsNullOrWhiteSpace(_newUrl) &&
        Uri.TryCreate(_newUrl.Trim(), UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommandStates();
        }
    }

    public bool IsDark
    {
        get => _isDark;
        set
        {
            if (!SetProperty(ref _isDark, value)) return;
            OnPropertyChanged(nameof(ThemeLabel));
            OnPropertyChanged(nameof(ThemeModeLabel));
            OnPropertyChanged(nameof(FollowsSystemTheme));
        }
    }

    /// <summary>
    /// True bila tema masih mengikuti Windows. Null berarti pengguna pernah
    /// memilih sendiri, jadi perubahan tema Windows tidak boleh menimpanya.
    /// </summary>
    public bool? DarkThemeOverride => _settings.DarkTheme;

    public bool FollowsSystemTheme => _settings.DarkTheme is null;

    public string ThemeLabel => IsDark ? "Tema terang" : "Tema gelap";

    /// <summary>Label yang menjelaskan sumber tema saat ini.</summary>
    public string ThemeModeLabel => FollowsSystemTheme
        ? (IsDark ? "Ikuti Windows (gelap)" : "Ikuti Windows (terang)")
        : (IsDark ? "Dipilih manual (gelap)" : "Dipilih manual (terang)");

    public DownloadItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem is not null) _selectedItem.IsSelected = false;
            if (!SetProperty(ref _selectedItem, value)) return;

            if (value is not null)
            {
                value.IsSelected = true;
                IsInspectorOpen = true;
            }

            RefreshCommandStates();
        }
    }

    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        private set => SetProperty(ref _isInspectorOpen, value);
    }

    public AsyncRelayCommand AddCommand { get; }
    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand PauseCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public AsyncRelayCommand ResumeAllCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand CloseInspectorCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand FocusUrlCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public RelayCommand ClearFilterCommand { get; }

    public async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_settings.DownloadDirectory);
            await Manager.RestoreAsync(_cts.Token).ConfigureAwait(true);
            Downloads.Reload(Manager.Tasks);
            RefreshCommandStates();

            if (Manager.Tasks.Count > 0)
            {
                StatusMessage = $"{Manager.Tasks.Count} unduhan dipulihkan, semua dalam keadaan dijeda";
            }
        }
        catch (OperationCanceledException)
        {
            // Aplikasi ditutup saat memulihkan.
        }
        catch (Exception ex)
        {
            StatusMessage = "Gagal memulihkan sesi: " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task AddAndStartAsync()
    {
        DownloadTask? task = TryAdd();
        if (task is null) return;
        await StartTaskAsync(task).ConfigureAwait(true);
    }

    private void AddOnly()
    {
        DownloadTask? task = TryAdd();
        if (task is null) return;

        StatusMessage = $"{task.FileName} masuk antrean";
    }

    /// <summary>
    /// Terima URL dari endpoint lokal (bookmarklet). Tidak lewat AddCommand
    /// supaya hasilnya bisa dilaporkan ke browser, bukan diam bila tombol
    /// tidak bisa jalan; dan tidak menyentuh kotak URL karena pengguna tidak
    /// sedang mengetik di sana.
    /// </summary>
    public async System.Threading.Tasks.Task<(bool Ok, string Message)> AddFromLocalEndpointAsync(
        LocalEndpointServer.AddRequest request)
    {
        try
        {
            if (_exclusions.IsExcluded(request.Url))
                return (false, "URL ada di daftar pengecualian");

            Directory.CreateDirectory(_settings.DownloadDirectory);

            DownloadTask task = Manager.Add(request.Url, _settings.DownloadDirectory,
                referer: request.Referer, cookies: request.Cookies);
            DownloadItemViewModel item = Downloads.Add(task);
            SelectedItem = item;
            RefreshCommandStates();

            if (_settings.StartDownloadsAutomatically)
            {
                await StartTaskAsync(task).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = $"{task.FileName} masuk antrean dari browser";
            }

            return (true, task.FileName);
        }
        catch (DownloadException ex)
        {
            StatusMessage = ex.Message;
            return (false, ex.Message);
        }
    }

    private DownloadTask? TryAdd()
    {
        string url = (_newUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            Directory.CreateDirectory(_settings.DownloadDirectory);

            DownloadTask task = Manager.Add(url, _settings.DownloadDirectory);
            DownloadItemViewModel item = Downloads.Add(task);
            SelectedItem = item;
            NewUrl = "";
            StatusMessage = $"Ditambahkan: {task.FileName}";
            RefreshCommandStates();
            return task;
        }
        catch (DownloadException ex)
        {
            StatusMessage = ex.Message;
            return null;
        }
    }

    private async System.Threading.Tasks.Task StartTaskAsync(DownloadTask task)
    {
        IsBusy = true;
        StatusMessage = $"Memeriksa {task.FileName}";
        try
        {
            await Manager.StartAsync(task.Id, _cts.Token).ConfigureAwait(true);

            DownloadItemViewModel? item = Downloads.Items.FirstOrDefault(i => i.Id == task.Id);
            item?.RefreshFromTask();
            // Nama berkas bisa berubah setelah probe; kategori mengikuti nama.
            Downloads.ApplyFilterAndSort();

            StatusMessage = task.State switch
            {
                DownloadState.Completed => $"{task.FileName} selesai",
                DownloadState.Failed => $"Gagal: {task.LastError}",
                DownloadState.Paused => $"{task.FileName} dijeda, progres tersimpan",
                _ => "Siap",
            };

            if (task.State == DownloadState.Completed && item is not null)
            {
                // Riwayat dicatat di sini: satu titik untuk semua jalur mulai
                // (tambah manual, resume, lanjutkan semua, clipboard).
                _history.Record(new HistoryEntry
                {
                    Id = task.Id,
                    FileName = task.FileName,
                    Url = task.Url.ToString(),
                    FinalPath = task.FinalPath,
                    TotalBytes = task.TotalLength,
                    Category = FileCategories.LabelOf(task.FileName),
                });

                DownloadCompleted?.Invoke(item);
            }
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    public async System.Threading.Tasks.Task StartByIdAsync(string id)
    {
        DownloadTask? task = Manager.Get(id);
        if (task is null) return;
        await StartTaskAsync(task).ConfigureAwait(true);
    }

    private void PauseSelected()
    {
        if (SelectedItem is null) return;
        Manager.Pause(SelectedItem.Id);
        StatusMessage = $"{SelectedItem.FileName} dijeda, progres tersimpan";
        RefreshCommandStates();
    }

    private async System.Threading.Tasks.Task ResumeSelectedAsync()
    {
        if (SelectedItem is null) return;
        await StartByIdAsync(SelectedItem.Id).ConfigureAwait(true);
    }

    private void CancelSelected()
    {
        if (SelectedItem is null) return;
        string id = SelectedItem.Id;
        string name = SelectedItem.FileName;
        Manager.Cancel(id);
        Downloads.Remove(id);
        SelectedItem = null;
        IsInspectorOpen = false;
        StatusMessage = $"{name} dibatalkan dan berkas sementara dihapus";
        RefreshCommandStates();
    }

    private void PauseAll()
    {
        foreach (DownloadItemViewModel item in Downloads.Items.Where(i => i.CanPause))
            Manager.Pause(item.Id);

        StatusMessage = "Semua unduhan aktif dijeda";
        RefreshCommandStates();
    }

    private async System.Threading.Tasks.Task ResumeAllAsync()
    {
        List<DownloadItemViewModel> pending = Downloads.Items.Where(i => i.CanResume).ToList();
        foreach (DownloadItemViewModel item in pending)
        {
            while (IsBusy) await System.Threading.Tasks.Task.Delay(150).ConfigureAwait(true);
            await StartByIdAsync(item.Id).ConfigureAwait(true);
        }

        StatusMessage = $"{pending.Count} unduhan dilanjutkan";
    }

    private void ClearCompleted()
    {
        List<DownloadItemViewModel> done = Downloads.Items.Where(i => i.State == DownloadState.Completed).ToList();
        foreach (DownloadItemViewModel item in done)
            Downloads.Remove(item.Id);

        if (SelectedItem is not null && done.Contains(SelectedItem))
        {
            SelectedItem = null;
            IsInspectorOpen = false;
        }

        StatusMessage = $"{done.Count} entri selesai dihapus dari daftar";
        RefreshCommandStates();
    }

    private void CopySelectedUrl()
    {
        if (SelectedItem is null) return;
        try
        {
            Clipboard.SetText(SelectedItem.Url);
            StatusMessage = "URL disalin ke clipboard";
        }
        catch (Exception)
        {
            StatusMessage = "Clipboard sedang dipakai aplikasi lain";
        }
    }

    private void OpenSelectedLocation()
    {
        if (SelectedItem is null) return;

        string target = SelectedItem.State == DownloadState.Completed && File.Exists(SelectedItem.Task.FinalPath)
            ? SelectedItem.Task.FinalPath
            : _settings.DownloadDirectory;

        OpenInShell(target);
    }

    private void OpenDownloadFolder() => OpenInShell(_settings.DownloadDirectory);

    private void OpenInShell(string path)
    {
        try
        {
            if (path == _settings.DownloadDirectory) Directory.CreateDirectory(path);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "Tidak bisa membuka lokasi: " + ex.Message;
        }
    }

    private void ToggleTheme()
    {
        // Satu klik bercycle tiga mode: gelap, terang, lalu kembali mengikuti
        // Windows. Dengan begitu "ikuti sistem" selalu bisa dicapai tanpa
        // perlu pengaturan terpisah.
        if (_settings.DarkTheme is null)
        {
            // Belum pernah memilih: tetapkan mode yang berlawanan dengan sistem.
            _settings.DarkTheme = !SystemThemeWatcher.IsSystemDark();
        }
        else if (_settings.DarkTheme != SystemThemeWatcher.IsSystemDark())
        {
            // Manual dan berbeda dari sistem: kembalikan ke sistem.
            _settings.DarkTheme = null;
        }
        else
        {
            // Manual dan sama dengan sistem: pilih kebalikannya.
            _settings.DarkTheme = !_settings.DarkTheme.Value;
        }

        IsDark = _settings.DarkTheme ?? SystemThemeWatcher.IsSystemDark();
        _settings.Save();

        OnPropertyChanged(nameof(DarkThemeOverride));
        OnPropertyChanged(nameof(ThemeModeLabel));
        OnPropertyChanged(nameof(FollowsSystemTheme));
    }

    /// <summary>
    /// Terapkan tema sistem yang baru terdeteksi. Hanya dipanggil oleh
    /// pemantau tema, dan tidak melakukan apa pun bila pengguna sudah memilih
    /// tema sendiri.
    /// </summary>
    public void ApplySystemTheme(bool dark)
    {
        if (_settings.DarkTheme is not null) return;
        if (IsDark == dark) return;

        IsDark = dark;
        OnPropertyChanged(nameof(DarkThemeOverride));
        OnPropertyChanged(nameof(ThemeModeLabel));
        OnPropertyChanged(nameof(FollowsSystemTheme));
    }

    /// <summary>Dipanggil dari pemantau clipboard saat URL baru terdeteksi.</summary>
    public void OnClipboardUrl(string url)
    {
        if (_exclusions.IsExcluded(url))
        {
            StatusMessage = "URL diabaikan (ada di daftar pengecualian)";
            return;
        }

        NewUrl = url;
        StatusMessage = "URL terdeteksi di clipboard";
    }

    /// <summary>Baca ulang daftar pengecualian setelah berkasnya diedit.</summary>
    public void ReloadExclusions() => _exclusions.Reload();

    /// <summary>Terapkan batas kecepatan dari pengaturan terbaru.</summary>
    public void TerapkanBatasKecepatan() =>
        Manager.SetSpeedLimit((long)_settings.SpeedLimitKbPerSecond * 1024);

    public void SetStatus(string message) => StatusMessage = message;

    public void RefreshCommandStates()
    {
        AddCommand.RaiseCanExecuteChanged();
        AddToQueueCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        OpenFileCommand.RaiseCanExecuteChanged();
        PauseAllCommand.RaiseCanExecuteChanged();
        ResumeAllCommand.RaiseCanExecuteChanged();
        ClearCompletedCommand.RaiseCanExecuteChanged();
        CopyUrlCommand.RaiseCanExecuteChanged();
        ClearFilterCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Downloads.StopSampling();
        _cts.Cancel();
        Downloads.Dispose();
        _ = Manager.DisposeAsync();
        _cts.Dispose();
    }
}






