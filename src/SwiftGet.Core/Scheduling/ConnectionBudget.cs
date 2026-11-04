namespace SwiftGet.Core.Scheduling;

/// <summary>
/// Pengaturan global yang memengaruhi seberapa agresif unduhan berjalan.
/// Nilai default sengaja konservatif: 8 koneksi per file dan 3 file bersamaan
/// sudah cukup untuk memenuhi bandwidth rumah tanpa memicu rate-limit server.
/// </summary>
public sealed class SchedulerOptions
{
    /// <summary>Batas unduhan yang boleh berjalan bersamaan.</summary>
    public int MaxConcurrentDownloads { get; init; } = 3;

    /// <summary>Batas koneksi paralel per file.</summary>
    public int MaxConnectionsPerDownload { get; init; } = 8;

    /// <summary>
    /// Batas koneksi paralel per host. Server yang sama tidak boleh dihujani
    /// melebihi angka ini meskipun ada beberapa unduhan aktif dari host itu.
    /// </summary>
    public int MaxConnectionsPerHost { get; init; } = 12;

    /// <summary>
    /// Lokasi ffmpeg.exe untuk menggabungkan jalur video dan audio DASH.
    /// Kosong berarti dicari di folder aplikasi lalu PATH.
    /// </summary>
    public string? FfmpegPath { get; init; }

    /// <summary>
    /// Pindahkan berkas selesai ke subfolder per kategori di bawah folder
    /// unduhan (Video, Arsip, dst) — pola kategori IDM.
    /// </summary>
    public bool CategoryFolders { get; init; }
}

/// <summary>
/// Pembagi izin koneksi: satu semaphore global dan satu semaphore per host.
/// <para>
/// Setiap worker segmen harus memegang <b>keduanya</b> selama aktif. Ini meniru
/// prinsip yang dipakai IDM (peringatannya: terlalu banyak koneksi dari satu
/// klien bisa membuat server mem-blacklist), tetapi diimplementasikan dari nol.
/// </para>
/// </summary>
public sealed class ConnectionBudget : IDisposable
{
    private readonly SemaphoreSlim _global;
    private readonly SemaphoreSlim _globalDownloads;
    private readonly Dictionary<string, SemaphoreSlim> _perHost;
    private readonly SchedulerOptions _options;
    private readonly object _hostGate = new();
    private bool _disposed;

    public ConnectionBudget(SchedulerOptions? options = null)
    {
        _options = options ?? new SchedulerOptions();
        _globalDownloads = new SemaphoreSlim(_options.MaxConcurrentDownloads);
        _global = new SemaphoreSlim(_options.MaxConnectionsPerDownload * _options.MaxConcurrentDownloads);
        _perHost = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
    }

    public SchedulerOptions Options => _options;

    /// <summary>
    /// Pembatas kecepatan total yang dibagi semua sesi (Speed Limiter).
    /// </summary>
    public SpeedLimiter Limiter { get; } = new();

    /// <summary>Ambil izin untuk satu file yang akan mulai diunduh.</summary>
    public Task EnterDownloadAsync(CancellationToken ct) => _globalDownloads.WaitAsync(ct);

    public void ExitDownload() => _globalDownloads.Release();

    /// <summary>
    /// Ambil izin koneksi untuk satu worker segmen pada host tertentu.
    /// Urutan akuisisi selalu global dulu lalu host, untuk menghindari deadlock.
    /// </summary>
    public async Task EnterConnectionAsync(string host, CancellationToken ct)
    {
        await _global.WaitAsync(ct).ConfigureAwait(false);

        var hostSemaphore = GetHostSemaphore(host);
        try
        {
            await hostSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _global.Release();
            throw;
        }
    }

    public void ExitConnection(string host)
    {
        var hostSemaphore = GetHostSemaphore(host);
        hostSemaphore.Release();
        _global.Release();
    }

    private SemaphoreSlim GetHostSemaphore(string host)
    {
        lock (_hostGate)
        {
            string key = string.IsNullOrWhiteSpace(host) ? "unknown" : host;
            if (!_perHost.TryGetValue(key, out var sem))
            {
                sem = new SemaphoreSlim(_options.MaxConnectionsPerHost);
                _perHost[key] = sem;
            }
            return sem;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _global.Dispose();
        _globalDownloads.Dispose();

        lock (_hostGate)
        {
            foreach (var sem in _perHost.Values) sem.Dispose();
            _perHost.Clear();
        }
    }
}
