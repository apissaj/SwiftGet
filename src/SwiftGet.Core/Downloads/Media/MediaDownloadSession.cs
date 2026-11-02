using System.Text;
using SwiftGet.Core.Http;
using SwiftGet.Core.Scheduling;
using SwiftGet.Core.Storage;

namespace SwiftGet.Core.Downloads.Media;

/// <summary>
/// Sesi unduhan playlist media (HLS/DASH): ambil playlist, pilih varian
/// terbaik, unduh segmen dengan beberapa koneksi, lalu tuliskan berurutan
/// ke satu berkas.
/// <para>
/// Segmen media harus tersusun berurutan di berkas hasil, tetapi unduhannya
/// boleh paralel: kolam worker mengambil segmen dari depan, hasil yang
/// selesai menunggu giliran di memori (jendela kecil, segmen HLS biasanya
/// puluhan KB hingga beberapa MB), dan satu penulis menambahkannya ke
/// berkas. Progres yang sudah tertulis disimpan sebagai index segmen, jadi
/// resume melanjutkan dari segmen berikutnya — berbeda dari unduhan Range
/// yang melanjutkan byte.
/// </para>
/// </summary>
public sealed class MediaDownloadSession : IDownloadSession
{
    private const long MaxPlaylistBytes = 2 * 1024 * 1024;
    private const int CopyBufferSize = 256 * 1024;

    private readonly DownloadTask _task;
    private readonly HttpRangeClient _http;
    private readonly ConnectionBudget _budget;
    private readonly ManifestStore _store;
    private readonly int _maxConnections;
    private readonly RetryPolicy _retry;
    private readonly Action<DownloadProgress>? _onProgress;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private Task? _runner;
    private volatile bool _paused;
    private volatile bool _canceled;
    private bool _disposed;

    // Keadaan progres yang diperbarui oleh penulis; dibaca oleh Publish().
    private readonly SpeedMeter _speed = new();
    private long _bytesWritten;
    private int _segmentsWritten;
    private int _segmentTotal;
    private long _lastManifestWriteTicks;
    private readonly object _manifestWriteGate = new();

    public MediaDownloadSession(
        DownloadTask task, HttpRangeClient http, ConnectionBudget budget,
        ManifestStore store, int maxConnections,
        RetryPolicy? retry = null, Action<DownloadProgress>? onProgress = null)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _maxConnections = Math.Max(1, maxConnections);
        _retry = retry ?? new RetryPolicy();
        _onProgress = onProgress;
    }

    public DownloadTask Task => _task;

    public async System.Threading.Tasks.Task RunAsync(CancellationToken externalToken = default)
    {
        if (_runner is { IsCompleted: false })
            throw new InvalidOperationException("Unduhan ini sedang berjalan.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _cts.Token);

        _paused = false;
        _canceled = false;
        _runner = RunCoreAsync(linked.Token);
        await _runner.ConfigureAwait(false);
    }

    /// <summary>Hentikan sementara; progres yang sudah tertulis tetap disimpan.</summary>
    public void Pause()
    {
        _paused = true;
        _cts.Cancel();
    }

    /// <summary>Batalkan dan hapus state supaya unduhan ini hilang sepenuhnya.</summary>
    public void Cancel()
    {
        _canceled = true;
        _paused = false;
        _cts.Cancel();
    }

    private async System.Threading.Tasks.Task RunCoreAsync(CancellationToken ct)
    {
        try
        {
            _task.SetState(DownloadState.Probing);
            Publish();

            MediaPlaylist playlist = await FetchPlaylistAsync(_task.Url, ct).ConfigureAwait(false);

            if (playlist.Kind == MediaPlaylistKind.Master)
            {
                // Ambil kualitas terbaik yang ditawarkan; pemilihan kualitas
                // manual adalah kebijakan UI, bukan sesi.
                MediaVariant variant = HlsPlaylistParser.PickVariant(playlist);
                playlist = await FetchPlaylistAsync(variant.Url, ct).ConfigureAwait(false);
            }

            if (playlist.Kind != MediaPlaylistKind.Media || playlist.Segments.Count == 0)
                throw new DownloadException(DownloadErrorKind.CorruptResponse,
                    "Playlist tidak memuat segmen media apa pun.");

            _task.FinalUri = playlist.BaseUri;
            _task.IsMedia = true;
            _task.SupportsRanges = false;
            _task.TotalLength = null;
            _task.MediaSegmentCount = playlist.Segments.Count;
            _segmentTotal = playlist.Segments.Count;
            _task.FileName = FinalFileName(playlist);

            string partPath = _store.GetPartPath(_task.Id);
            int startIndex = ResolveResumeIndex(partPath, playlist);

            await _budget.EnterDownloadAsync(ct).ConfigureAwait(false);
            try
            {
                await DownloadSegmentsAsync(playlist, partPath, startIndex, ct).ConfigureAwait(false);
            }
            finally
            {
                _budget.ExitDownload();
            }

            if (_paused)
            {
                _task.SetState(DownloadState.Paused);
                Publish();
                await SaveManifestSafeAsync(ct).ConfigureAwait(false);
                return;
            }

            if (_canceled)
            {
                _task.SetState(DownloadState.Canceled);
                Publish();
                return;
            }

            await FinalizeAsync(partPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_canceled)
        {
            _task.SetState(DownloadState.Canceled);
            Publish();
        }
        catch (OperationCanceledException)
        {
            _task.SetState(DownloadState.Paused);
            Publish();
            await SaveManifestSafeAsync(ct).ConfigureAwait(false);
        }
        catch (DownloadException ex)
        {
            _task.SetState(DownloadState.Failed, ex.Message);
            Publish();
            await SaveManifestSafeAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _task.SetState(DownloadState.Failed, "Gagal menulis berkas: " + ex.Message);
            Publish();
            await SaveManifestSafeAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Ambil teks playlist dan uraikan sesuai bentuknya.</summary>
    private async Task<MediaPlaylist> FetchPlaylistAsync(Uri url, CancellationToken ct)
    {
        using FullResponse response = await _http.GetAsync(url, ct, _task.Referer).ConfigureAwait(false);
        string text = await ReadPlaylistTextAsync(response, ct).ConfigureAwait(false);

        string trimmed = text.TrimStart();
        if (trimmed.StartsWith("#EXTM3U", StringComparison.Ordinal))
            return HlsPlaylistParser.Parse(text, response.FinalUri);

        if (trimmed.StartsWith('<'))
            return DashPlaylistParser.Parse(text, response.FinalUri);

        throw new DownloadException(DownloadErrorKind.CorruptResponse,
            "URL ini tidak berisi playlist HLS atau DASH yang dikenal.");
    }

    private static async Task<string> ReadPlaylistTextAsync(FullResponse response, CancellationToken ct)
    {
        await using Stream stream = response.GetStream();
        using var buffer = new MemoryStream();

        byte[] chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxPlaylistBytes)
                throw new DownloadException(DownloadErrorKind.CorruptResponse,
                    "Playlist terlalu besar untuk diproses.");

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Nama berkas akhir: playlist .m3u8/.mpd diganti ekstensi segmen
    /// pertamanya, supaya hasil gabungan tidak bernama "index.m3u8".
    /// </summary>
    private static string FinalFileName(MediaPlaylist playlist)
    {
        string ext = Path.GetExtension(playlist.Segments[0].Url.AbsolutePath);
        if (string.IsNullOrEmpty(ext))
            ext = playlist.BaseUri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
                ? ".mp4" : ".ts";

        // .m4s adalah potongan fMP4; berkas gabungannya pantas bernama .mp4.
        if (ext.Equals(".m4s", StringComparison.OrdinalIgnoreCase)) ext = ".mp4";

        return Path.ChangeExtension(
            (playlist.BaseUri.AbsolutePath.Split('/').LastOrDefault(s => s.Length > 0)) ?? "media",
            ext);
    }

    /// <summary>
    /// Tentukan segmen pertama yang perlu diunduh lagi. Resume hanya sah bila
    /// jumlah segmen playlist masih sama; playlist yang berbeda berarti
    /// kontennya berganti, jadi mulai dari nol.
    /// </summary>
    private int ResolveResumeIndex(string partPath, MediaPlaylist playlist)
    {
        bool canResume = _task.MediaResumeIndex is int resume && resume > 0 &&
                         _task.MediaSegmentCount == playlist.Segments.Count &&
                         File.Exists(partPath);

        if (!canResume)
        {
            try { File.Delete(partPath); }
            catch (IOException) { /* berkas baru akan menimpanya saat dibuat */ }
            catch (UnauthorizedAccessException) { }

            _task.MediaResumeIndex = 0;
            _bytesWritten = 0;
            _segmentsWritten = 0;
            return 0;
        }

        _segmentsWritten = _task.MediaResumeIndex!.Value;
        return _segmentsWritten;
    }

    private async System.Threading.Tasks.Task DownloadSegmentsAsync(
        MediaPlaylist playlist, string partPath, int startIndex, CancellationToken ct)
    {
        IReadOnlyList<MediaSegmentEntry> segments = playlist.Segments;
        int total = segments.Count;

        await using var output = new FileStream(
            partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        output.Seek(0, SeekOrigin.End);
        _bytesWritten = output.Length;
        _segmentsWritten = startIndex;
        _task.MediaResumeIndex = startIndex;

        _task.SetState(DownloadState.Downloading);
        Publish();

        // Koordinasi worker-penulis. Urutan penguncian: _fetchGate saja.
        var fetchGate = new object();
        var completions = new Dictionary<int, TaskCompletionSource<byte[]>>();
        int fetchCursor = startIndex;
        int workerCount = Math.Min(_maxConnections, Math.Max(1, total - startIndex));

        System.Threading.Tasks.Task Worker()
        {
            return System.Threading.Tasks.Task.Run(async () =>
            {
                while (true)
                {
                    int index;
                    lock (fetchGate)
                    {
                        if (fetchCursor >= total || _paused || _canceled) return;
                        index = fetchCursor++;
                    }

                    byte[] data = await FetchSegmentAsync(segments[index].Url, ct).ConfigureAwait(false);

                    lock (fetchGate)
                    {
                        if (!completions.TryGetValue(index, out TaskCompletionSource<byte[]>? tcs))
                        {
                            tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                            completions[index] = tcs;
                        }
                        tcs.TrySetResult(data);
                    }
                }
            }, ct);
        }

        var workers = new List<System.Threading.Tasks.Task>(workerCount);
        for (int i = 0; i < workerCount; i++)
            workers.Add(Worker());

        // Penulis tunggal: segmen ditambahkan persis berurutan, apa pun urutan
        // kedatangannya. Segmen yang belum tiba membuat penulis menunggu.
        try
        {
            while (_segmentsWritten < total && !_paused && !_canceled)
            {
                TaskCompletionSource<byte[]> tcs;
                lock (fetchGate)
                {
                    if (!completions.TryGetValue(_segmentsWritten, out tcs!))
                    {
                        tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                        completions[_segmentsWritten] = tcs;
                    }
                }

                byte[] data = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

                if (data.Length > 0)
                    await output.WriteAsync(data, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);

                _bytesWritten += data.Length;
                _segmentsWritten++;
                _task.MediaResumeIndex = _segmentsWritten;
                _speed.Record(data.Length);
                Publish();

                await SaveManifestThrottledAsync(ct).ConfigureAwait(false);
                lock (fetchGate) completions.Remove(_segmentsWritten - 1);
            }
        }
        catch (OperationCanceledException) when (_failure is not null)
        {
            // Worker mengalami galat permanen dan membatalkan semuanya;
            // lempar galat aslinya, jangan dianggap jeda.
            throw _failure;
        }

        await System.Threading.Tasks.Task.WhenAll(workers).ConfigureAwait(false);
    }

    private DownloadException? _failure;

    private async Task<byte[]> FetchSegmentAsync(Uri url, CancellationToken ct)
    {
        string host = url.Host;

        for (int attempt = 1; ; attempt++)
        {
            bool entered = false;
            try
            {
                await _budget.EnterConnectionAsync(host, ct).ConfigureAwait(false);
                entered = true;

                using FullResponse response = await _http.GetAsync(url, ct, _task.Referer).ConfigureAwait(false);
                long? expected = response.Response.Content.Headers.ContentLength;

                byte[] data;
                try
                {
                    await using Stream stream = response.GetStream();
                    using var memory = new MemoryStream();
                    await stream.CopyToAsync(memory, CopyBufferSize, ct).ConfigureAwait(false);
                    data = memory.ToArray();
                }
                catch (IOException ex)
                {
                    // Server menutup koneksi di tengah segmen. Tanpa konversi
                    // ini worker mati diam dan penulis menunggu selamanya;
                    // galat koneksi berarti layak dicoba ulang.
                    throw new DownloadException(DownloadErrorKind.ConnectionFailed,
                        $"Koneksi terputus saat membaca segmen: {ex.Message}", ex);
                }

                if (expected is long want && data.Length < want)
                    throw new DownloadException(DownloadErrorKind.ConnectionFailed,
                        $"Segmen terpotong: diharapkan {want} byte, diterima {data.Length}.");

                return data;
            }
            catch (DownloadException ex) when (RetryPolicy.IsTransient(ex))
            {
                if (attempt >= _retry.MaxAttempts)
                {
                    // Segmen yang sama gagal terus; tidak ada gunanya berputar
                    // tanpa akhir — hentikan sesi seperti unduhan biasa.
                    _failure ??= new DownloadException(ex.Kind,
                        $"Segmen {Path.GetFileName(url.AbsolutePath)} gagal setelah " +
                        $"{attempt} percobaan: {ex.Message}", ex);
                    _cts.Cancel();
                    throw new OperationCanceledException();
                }

                await System.Threading.Tasks.Task.Delay(_retry.GetDelay(attempt), ct).ConfigureAwait(false);
            }
            finally
            {
                if (entered) _budget.ExitConnection(host);
            }
        }
    }

    private async System.Threading.Tasks.Task FinalizeAsync(string partPath, CancellationToken ct)
    {
        File.Move(partPath, _task.FinalPath, overwrite: true);

        _task.SetState(DownloadState.Completed);
        _task.MediaResumeIndex = _segmentTotal;
        Publish();

        _store.Delete(_task.Id);
        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
    }

    private DownloadProgress Snapshot() => new()
    {
        Id = _task.Id,
        State = _task.State,
        BytesDownloaded = _bytesWritten,
        TotalBytes = null, // playlist tidak menjanjikan ukuran total di muka
        BytesPerSecond = _speed.GetRate(),
        CompletedSegments = _segmentsWritten,
        TotalSegments = _segmentTotal,
    };

    private void Publish() => _onProgress?.Invoke(Snapshot());

    private async System.Threading.Tasks.Task SaveManifestThrottledAsync(CancellationToken ct)
    {
        long now = Environment.TickCount64;
        lock (_manifestWriteGate)
        {
            if (now - _lastManifestWriteTicks < 1000) return;
            _lastManifestWriteTicks = now;
        }

        await SaveManifestSafeAsync(ct).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task SaveManifestSafeAsync(CancellationToken ct = default)
    {
        try
        {
            await _manifestGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _store.SaveAsync(_task.ToManifest(), ct).ConfigureAwait(false);
            }
            finally
            {
                _manifestGate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // Manifest gagal ditulis bukan alasan menggagalkan unduhan; percobaan
            // berikutnya (jeda, selesai) menulis ulang.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_runner is { IsCompleted: false })
        {
            _cts.Cancel();
            try { await _runner.ConfigureAwait(false); } catch { /* diabaikan saat dispose */ }
        }

        _cts.Dispose();
        _manifestGate.Dispose();
    }
}
