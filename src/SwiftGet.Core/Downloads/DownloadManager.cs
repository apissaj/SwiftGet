using System.Collections.Concurrent;
using SwiftGet.Core.Http;
using SwiftGet.Core.Scheduling;
using SwiftGet.Core.Storage;

namespace SwiftGet.Core.Downloads;

/// <summary>
/// Manajer seluruh unduhan: antrian, batas konkurensi, dan pemulihan sesi.
/// <para>
/// Ini satu-satunya tipe yang seharusnya disentuh lapisan UI. Semua akses ke
/// daftar internal dijaga lock, dan semua event dikirim sebagai snapshot.
/// </para>
/// </summary>
public sealed class DownloadManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<string, DownloadSession> _sessions = new();
    private readonly SemaphoreSlim _runGate;
    private readonly ConnectionBudget _budget;
    private readonly ManifestStore _store;
    private readonly HttpRangeClient _http;
    private readonly SchedulerOptions _options;
    private bool _disposed;

    public DownloadManager(string stateDirectory, SchedulerOptions? options = null)
    {
        _options = options ?? new SchedulerOptions();
        _store = new ManifestStore(stateDirectory);
        _budget = new ConnectionBudget(_options);
        _http = new HttpRangeClient();
        _runGate = new SemaphoreSlim(_options.MaxConcurrentDownloads);
    }

    /// <summary>Dipicu setiap kali progres salah satu unduhan berubah.</summary>
    public event Action<DownloadProgress>? ProgressChanged;

    /// <summary>Dipicu saat status sebuah unduhan berubah ke status terminal.</summary>
    public event Action<string, DownloadState, string?>? StateChanged;

    public IReadOnlyCollection<DownloadTask> Tasks => _tasks.Values.ToArray();

    public DownloadTask? Get(string id) => _tasks.TryGetValue(id, out var t) ? t : null;

    /// <summary>Muat ulang unduhan yang belum selesai dari folder state.</summary>
    public async System.Threading.Tasks.Task RestoreAsync(CancellationToken ct = default)
    {
        IReadOnlyList<DownloadManifest> manifests = await _store.LoadAllAsync(ct).ConfigureAwait(false);

        foreach (DownloadManifest manifest in manifests)
        {
            if (manifest.State is DownloadState.Completed or DownloadState.Canceled)
            {
                // Sudah beres; bersihkan sisa state tanpa memunculkannya di UI.
                _store.Delete(manifest.Id);
                continue;
            }

            try
            {
                DownloadTask task = DownloadTask.FromManifest(manifest);
                task.SetState(DownloadState.Paused);
                _tasks[task.Id] = task;
            }
            catch (UriFormatException)
            {
                _store.Delete(manifest.Id);
            }
        }
    }

    /// <summary>
    /// Tambahkan unduhan baru. Nama file akhir ditentukan dari <c>Content-Disposition</c>
    /// bila tersedia, jika tidak dari URL.
    /// </summary>
    public DownloadTask Add(string url, string targetDirectory, string? fileName = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new DownloadException(DownloadErrorKind.InvalidUrl, $"URL tidak valid: {url}");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new DownloadException(DownloadErrorKind.InvalidUrl,
                $"Hanya http/https yang didukung, bukan '{uri.Scheme}'.");

        var task = new DownloadTask(Guid.NewGuid().ToString("N"), uri, targetDirectory, fileName);
        _tasks[task.Id] = task;
        return task;
    }

    /// <summary>
    /// Jalankan (atau lanjutkan) sebuah unduhan. Bila batas konkurensi sudah
    /// penuh, pemanggilan ini menunggu sampai ada slot.
    /// </summary>
    public async System.Threading.Tasks.Task StartAsync(string id, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(id, out DownloadTask? task))
            throw new InvalidOperationException($"Unduhan '{id}' tidak dikenal.");

        if (task.State.IsActive())
            return;

        if (task.State == DownloadState.Completed)
            return;

        DownloadSession session = _sessions.AddOrUpdate(
            id,
            _ => CreateSession(task),
            (_, existing) => existing);

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
            _sessions.TryRemove(id, out _);
        }

        StateChanged?.Invoke(id, task.State, task.LastError);
    }

    private DownloadSession CreateSession(DownloadTask task) =>
        new(task, _http, _budget, _store, _options.MaxConnectionsPerDownload,
            retry: null, onProgress: p => ProgressChanged?.Invoke(p));

    public void Pause(string id)
    {
        if (_sessions.TryGetValue(id, out DownloadSession? session))
            session.Pause();
    }

    public void Cancel(string id)
    {
        if (_sessions.TryGetValue(id, out DownloadSession? session))
            session.Cancel();

        if (_tasks.TryGetValue(id, out DownloadTask? task))
            task.SetState(DownloadState.Canceled);

        _store.Delete(id);
        _tasks.TryRemove(id, out _);
    }

    /// <summary>Hapus unduhan dari daftar tanpa menyentuh berkas yang sudah selesai.</summary>
    public void Remove(string id)
    {
        Cancel(id);
    }

    public DownloadProgress? GetProgress(string id) =>
        _tasks.TryGetValue(id, out DownloadTask? task) ? task.Snapshot() : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (DownloadSession session in _sessions.Values)
        {
            session.Pause();
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        _http.Dispose();
        _budget.Dispose();
        _runGate.Dispose();
    }
}
