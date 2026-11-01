using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SwiftGet.App.Infrastructure;
using SwiftGet.Core;
using SwiftGet.Core.Downloads;
using SwiftGet.Core.Scheduling;

namespace SwiftGet.App.ViewModels;

/// <summary>
/// View model jendela utama: daftar unduhan, perintah, dan status aplikasi.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();

    private string _newUrl = "";
    private string _statusMessage = "Siap.";
    private bool _isBusy;
    private bool _isDark;
    private DownloadItemViewModel? _selectedItem;
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

        AddCommand = new AsyncRelayCommand(AddAndStartAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(NewUrl));
        AddWithoutStartCommand = new AsyncRelayCommand(AddOnlyAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(NewUrl));
        PauseCommand = new RelayCommand(_ => PauseSelected(), _ => SelectedItem?.CanPause == true);
        ResumeCommand = new AsyncRelayCommand(_ => ResumeSelectedAsync(), _ => SelectedItem?.CanResume == true);
        CancelCommand = new RelayCommand(_ => CancelSelected(), _ => SelectedItem?.CanCancel == true);
        OpenFolderCommand = new RelayCommand(_ => OpenDownloadFolder(), _ => Directory.Exists(DownloadDir));
        PauseAllCommand = new RelayCommand(_ => PauseAll(), _ => Downloads.Items.Any(i => i.CanPause));
        ResumeAllCommand = new AsyncRelayCommand(ResumeAllAsync, () => Downloads.Items.Any(i => i.CanResume));
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        ClearCompletedCommand = new RelayCommand(_ => ClearCompleted(), _ => Downloads.Items.Any(i => i.State == DownloadState.Completed));
        CopyUrlCommand = new RelayCommand(_ => CopySelectedUrl(), _ => SelectedItem is not null);

        Downloads.Items.CollectionChanged += (_, _) => RefreshCommandStates();
    }

    public DownloadManager Manager { get; }

    public DownloadsViewModel Downloads { get; }

    public ObservableCollection<string> Log { get; } = new();

    public string DownloadDir => _settings.DownloadDirectory;

    public string NewUrl
    {
        get => _newUrl;
        set
        {
            if (!SetProperty(ref _newUrl, value)) return;
            RefreshCommandStates();
        }
    }

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
        private set
        {
            if (!SetProperty(ref _isDark, value)) return;
            OnPropertyChanged(nameof(ThemeLabel));
        }
    }

    public string ThemeLabel => IsDark ? "Terang" : "Gelap";

    public DownloadItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            RefreshCommandStates();
        }
    }

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand AddWithoutStartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public AsyncRelayCommand ResumeAllCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand CopyUrlCommand { get; }

    /// <summary>Diminta UI saat unduhan selesai (untuk notifikasi tray).</summary>
    public event Action<DownloadItemViewModel>? DownloadCompleted;

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
                StatusMessage = $"{Manager.Tasks.Count} unduhan dipulihkan.";
            }
            else
            {
                StatusMessage = "Siap. Tempel URL untuk memulai.";
            }
        }
        catch (OperationCanceledException) { /* keluar */ }
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

    private async System.Threading.Tasks.Task AddOnlyAsync()
    {
        if (TryAdd() is not null)
            await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(true);
    }

    private DownloadTask? TryAdd()
    {
        string url = (NewUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            Directory.CreateDirectory(_settings.DownloadDirectory);

            DownloadTask task = Manager.Add(url, _settings.DownloadDirectory);
            Downloads.Add(task);
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
        StatusMessage = $"Memeriksa {task.FileName}...";
        try
        {
            await Manager.StartAsync(task.Id, _cts.Token).ConfigureAwait(true);

            DownloadItemViewModel? item = Downloads.Items.FirstOrDefault(i => i.Id == task.Id);
            if (item is not null) item.RefreshFromTask();

            if (task.State == DownloadState.Completed)
            {
                StatusMessage = $"Selesai: {task.FileName}";
                if (item is not null) DownloadCompleted?.Invoke(item);
            }
            else if (task.State == DownloadState.Failed)
            {
                StatusMessage = $"Gagal: {task.LastError}";
            }
            else if (task.State == DownloadState.Paused)
            {
                StatusMessage = $"Dijeda: {task.FileName}";
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
        StatusMessage = $"Dijeda: {SelectedItem.FileName}";
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
        Manager.Cancel(id);
        Downloads.Remove(id);
        StatusMessage = "Unduhan dibatalkan.";
        RefreshCommandStates();
    }

    private void PauseAll()
    {
        foreach (DownloadItemViewModel item in Downloads.Items.Where(i => i.CanPause))
            Manager.Pause(item.Id);

        StatusMessage = "Semua unduhan aktif dijeda.";
        RefreshCommandStates();
    }

    private async System.Threading.Tasks.Task ResumeAllAsync()
    {
        var pending = Downloads.Items.Where(i => i.CanResume).ToList();
        foreach (DownloadItemViewModel item in pending)
        {
            while (IsBusy) await System.Threading.Tasks.Task.Delay(200).ConfigureAwait(true);
            await StartByIdAsync(item.Id).ConfigureAwait(true);
        }

        StatusMessage = $"Melanjutkan {pending.Count} unduhan.";
    }

    private void ClearCompleted()
    {
        var done = Downloads.Items.Where(i => i.State == DownloadState.Completed).ToList();
        foreach (DownloadItemViewModel item in done)
            Downloads.Remove(item.Id);

        StatusMessage = $"{done.Count} entri selesai dibersihkan dari daftar.";
        RefreshCommandStates();
    }

    private void CopySelectedUrl()
    {
        if (SelectedItem is null) return;
        try
        {
            Clipboard.SetText(SelectedItem.Url);
            StatusMessage = "URL disalin ke clipboard.";
        }
        catch (Exception)
        {
            StatusMessage = "Clipboard sedang tidak bisa diakses.";
        }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.DownloadDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _settings.DownloadDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "Tidak bisa membuka folder: " + ex.Message;
        }
    }

    private void ToggleTheme()
    {
        IsDark = !IsDark;
        ThemeManager.Apply(IsDark);
        OnPropertyChanged(nameof(ThemeLabel));
    }

    /// <summary>Dipanggil dari clipboard watcher saat URL baru terdeteksi.</summary>
    public void OnClipboardUrl(string url)
    {
        NewUrl = url;
        StatusMessage = "URL terdeteksi di clipboard.";
    }

    public void SetStatus(string message) => StatusMessage = message;

    public void RefreshCommandStates()
    {
        AddCommand.RaiseCanExecuteChanged();
        AddWithoutStartCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        PauseAllCommand.RaiseCanExecuteChanged();
        ResumeAllCommand.RaiseCanExecuteChanged();
        ClearCompletedCommand.RaiseCanExecuteChanged();
        CopyUrlCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        Downloads.Dispose();
        _ = Manager.DisposeAsync();
        _cts.Dispose();
    }
}
