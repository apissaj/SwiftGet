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
        };

        Manager = new DownloadManager(AppSettings.StateDirectory, options);
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

        Downloads.Items.CollectionChanged += (_, _) => RefreshCommandStates();
    }

    public DownloadManager Manager { get; }

    public DownloadsViewModel Downloads { get; }

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
        }
    }

    public string ThemeLabel => IsDark ? "Tema terang" : "Tema gelap";

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

            StatusMessage = task.State switch
            {
                DownloadState.Completed => $"{task.FileName} selesai",
                DownloadState.Failed => $"Gagal: {task.LastError}",
                DownloadState.Paused => $"{task.FileName} dijeda, progres tersimpan",
                _ => "Siap",
            };

            if (task.State == DownloadState.Completed && item is not null)
            {
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
        // ThemeManager dipanggil oleh pemilik tampilan (MainWindow), bukan dari sini:
        // satu sumber kebenaran untuk membaca palet.
        IsDark = !IsDark;
        _settings.DarkTheme = IsDark;
        _settings.Save();
    }

    /// <summary>Dipanggil dari pemantau clipboard saat URL baru terdeteksi.</summary>
    public void OnClipboardUrl(string url)
    {
        NewUrl = url;
        StatusMessage = "URL terdeteksi di clipboard";
    }

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




