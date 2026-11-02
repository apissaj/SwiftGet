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
/// <para>
/// DASH yang memisahkan video dan audio menghasilkan dua jalur: keduanya
/// diunduh satu per satu ke berkas sementara terpisah, lalu digabung dengan
/// ffmpeg (-c copy, tanpa encode ulang). Tanpa ffmpeg, sesi gagal lebih awal
/// dengan pesan yang menjelaskan cara mengaturnya.
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
    private readonly string? _ffmpegConfigured;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private Task? _runner;
    private volatile bool _paused;
    private volatile bool _canceled;
    private bool _disposed;

    /// <summary>Keadaan progres satu jalur; agregat keseluruhan dijumlahkan.</summary>
    private sealed class TrackState
    {
        public required MediaPlaylist Playlist { get; init; }
        public required string PartPath { get; init; }
        public int StartIndex { get; set; }
        public int Written { get; set; }
        public long Bytes { get; set; }
        public int Total { get; init; }
    }

    private readonly List<TrackState> _tracks = new();
    private readonly SpeedMeter _speed = new();
    private long _lastManifestWriteTicks;
    private readonly object _manifestWriteGate = new();

    private DownloadException? _failure;

    public MediaDownloadSession(
        DownloadTask task, HttpRangeClient http, ConnectionBudget budget,
        ManifestStore store, int maxConnections,
        RetryPolicy? retry = null, Action<DownloadProgress>? onProgress = null,
        string? ffmpegPath = null)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _maxConnections = Math.Max(1, maxConnections);
        _retry = retry ?? new RetryPolicy();
        _onProgress = onProgress;
        _ffmpegConfigured = ffmpegPath;
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

            List<MediaPlaylist> tracks = await FetchTracksAsync(_task.Url, ct).ConfigureAwait(false);
            if (tracks.Any(t => t.Segments.Count == 0))
                throw new DownloadException(DownloadErrorKind.CorruptResponse,
                    "Playlist tidak memuat segmen media apa pun.");

            _task.FinalUri = tracks[0].BaseUri;
            _task.IsMedia = true;
            _task.SupportsRanges = false;
            _task.TotalLength = null;
            _task.MediaSegmentCount = tracks[0].Segments.Count;
            _task.FileName = FinalFileName(tracks[0]);

            string partPath = _store.GetPartPath(_task.Id);
            string audioPartPath = partPath + ".audio";
            bool multi = tracks.Count > 1;
            string? ffmpeg = null;

            if (multi)
            {
                // Gagal lebih awal sebelum byte apa pun diunduh: menggabungkan
                // tanpa ffmpeg tidak mungkin, dan unduhan dua jalur jadi sia-sia.
                ffmpeg = FfmpegLocator.Find(_ffmpegConfigured)
                    ?? throw new DownloadException(DownloadErrorKind.UnsupportedMedia,
                        "Penggabungan video dan audio butuh ffmpeg. Atur lokasinya di " +
                        "pengaturan, letakkan ffmpeg.exe di folder aplikasi, atau " +
                        "tambahkan ke PATH.");
            }

            _tracks.Clear();
            if (multi)
            {
                List<(int Resume, int Count)>? saved = ParseTracksState(_task.MediaTracksState);
                bool canResume = saved is not null
                    && saved.Count == tracks.Count
                    && saved.Select(s => s.Count).SequenceEqual(tracks.Select(t => t.Segments.Count))
                    && File.Exists(partPath) && File.Exists(audioPartPath);

                if (!canResume)
                {
                    DeleteQuietly(partPath);
                    DeleteQuietly(audioPartPath);
                    _task.MediaTracksState = null;
                }

                for (int i = 0; i < tracks.Count; i++)
                {
                    _tracks.Add(new TrackState
                    {
                        Playlist = tracks[i],
                        PartPath = i == 0 ? partPath : audioPartPath,
                        StartIndex = canResume ? saved![i].Resume : 0,
                        Total = tracks[i].Segments.Count,
                    });
                }
            }
            else
            {
                _tracks.Add(new TrackState
                {
                    Playlist = tracks[0],
                    PartPath = partPath,
                    StartIndex = ResolveSingleResumeIndex(partPath, tracks[0]),
                    Total = tracks[0].Segments.Count,
                });
            }

            await _budget.EnterDownloadAsync(ct).ConfigureAwait(false);
            try
            {
                // Jalur dikerjakan satu per satu; di dalam jalur, segmen tetap
                // diunduh paralel. Penggabungan bersamaan dua jalur menunggu
                // kebutuhan nyata — kerumitannya belum membayar dirinya.
                foreach (TrackState track in _tracks)
                {
                    if (track.StartIndex >= track.Total) continue; // jalur tuntas; resume langsung ke tahap mux
                    await DownloadSegmentsAsync(track, ct).ConfigureAwait(false);
                }
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

            await FinalizeAsync(partPath, multi ? audioPartPath : null, ffmpeg, ct).ConfigureAwait(false);
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

    /// <summary>Ambil playlist (HLS/DASH) dan kembalikan jalur-jalurnya.</summary>
    private async Task<List<MediaPlaylist>> FetchTracksAsync(Uri url, CancellationToken ct)
    {
        using FullResponse response = await _http.GetAsync(url, ct, _task.Referer, _task.Cookies).ConfigureAwait(false);
        string text = await ReadPlaylistTextAsync(response, ct).ConfigureAwait(false);

        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('<'))
            return DashPlaylistParser.ParseTracks(text, response.FinalUri);

        if (!trimmed.StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "URL ini tidak berisi playlist HLS atau DASH yang dikenal.");

        MediaPlaylist playlist = HlsPlaylistParser.Parse(text, response.FinalUri);
        if (playlist.Kind == MediaPlaylistKind.Master)
        {
            // Ambil kualitas terbaik yang ditawarkan; pemilihan kualitas
            // manual adalah kebijakan UI, bukan sesi.
            MediaVariant variant = HlsPlaylistParser.PickVariant(playlist);
            using FullResponse media = await _http.GetAsync(variant.Url, ct, _task.Referer).ConfigureAwait(false);
            playlist = HlsPlaylistParser.Parse(
                await ReadPlaylistTextAsync(media, ct).ConfigureAwait(false), media.FinalUri);
        }

        return [playlist];
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
    /// Tentukan segmen pertama yang perlu diunduh lagi untuk unduhan satu
    /// jalur. Resume hanya sah bila jumlah segmen playlist masih sama;
    /// playlist yang berbeda berarti kontennya berganti, jadi mulai dari nol.
    /// </summary>
    private int ResolveSingleResumeIndex(string partPath, MediaPlaylist playlist)
    {
        bool canResume = _task.MediaResumeIndex is int resume && resume > 0 &&
                         _task.MediaSegmentCount == playlist.Segments.Count &&
                         File.Exists(partPath);

        if (!canResume)
        {
            DeleteQuietly(partPath);
            _task.MediaResumeIndex = 0;
            return 0;
        }

        return _task.MediaResumeIndex!.Value;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* berkas baru akan menimpanya saat dibuat */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Baca keadaan resume multi-jalur berformat "resume:jumlah,resume:jumlah".
    /// Salah bentuk berarti mulai dari nol, bukan gagal.
    /// </summary>
    private static List<(int Resume, int Count)>? ParseTracksState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;

        var result = new List<(int, int)>();
        foreach (string part in state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = part.Split(':');
            if (pair.Length != 2 ||
                !int.TryParse(pair[0], out int resume) ||
                !int.TryParse(pair[1], out int count))
                return null;
            result.Add((resume, count));
        }

        return result;
    }

    private string BuildTracksState() =>
        string.Join(",", _tracks.Select(t => $"{t.Written}:{t.Total}"));

    private async System.Threading.Tasks.Task DownloadSegmentsAsync(TrackState track, CancellationToken ct)
    {
        IReadOnlyList<MediaSegmentEntry> segments = track.Playlist.Segments;
        int total = track.Total;

        await using var output = new FileStream(
            track.PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        output.Seek(0, SeekOrigin.End);
        track.Bytes = output.Length;
        track.Written = track.StartIndex;
        if (_tracks.Count == 1) _task.MediaResumeIndex = track.StartIndex;

        _task.SetState(DownloadState.Downloading);
        Publish();

        // Koordinasi worker-penulis. Urutan penguncian selalu _fetchGate saja.
        var fetchGate = new object();
        var completions = new Dictionary<int, TaskCompletionSource<byte[]>>();
        int fetchCursor = track.StartIndex;
        int workerCount = Math.Min(_maxConnections, Math.Max(1, total - track.StartIndex));

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
            while (track.Written < total && !_paused && !_canceled)
            {
                TaskCompletionSource<byte[]> tcs;
                lock (fetchGate)
                {
                    if (!completions.TryGetValue(track.Written, out tcs!))
                    {
                        tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                        completions[track.Written] = tcs;
                    }
                }

                byte[] data = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

                if (data.Length > 0)
                    await output.WriteAsync(data, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);

                track.Bytes += data.Length;
                track.Written++;
                if (_tracks.Count == 1) _task.MediaResumeIndex = track.Written;
                _speed.Record(data.Length);
                Publish();

                await SaveManifestThrottledAsync(ct).ConfigureAwait(false);
                lock (fetchGate) completions.Remove(track.Written - 1);
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

                using FullResponse response = await _http.GetAsync(url, ct, _task.Referer, _task.Cookies).ConfigureAwait(false);
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

    private async System.Threading.Tasks.Task FinalizeAsync(
        string partPath, string? audioPartPath, string? ffmpeg, CancellationToken ct)
    {
        if (audioPartPath is null)
        {
            File.Move(partPath, _task.FinalPath, overwrite: true);
        }
        else
        {
            await FfmpegLocator.MuxAsync(ffmpeg!, partPath, audioPartPath, _task.FinalPath, ct)
                .ConfigureAwait(false);
            DeleteQuietly(partPath);
            DeleteQuietly(audioPartPath);
        }

        _task.SetState(DownloadState.Completed);
        _task.MediaResumeIndex = _tracks.Sum(t => t.Total);
        Publish();

        _store.Delete(_task.Id);
    }

    private DownloadProgress Snapshot() => new()
    {
        Id = _task.Id,
        State = _task.State,
        BytesDownloaded = _tracks.Sum(t => t.Bytes),
        TotalBytes = null, // playlist tidak menjanjikan ukuran total di muka
        BytesPerSecond = _speed.GetRate(),
        CompletedSegments = _tracks.Sum(t => t.Written),
        TotalSegments = _tracks.Sum(t => t.Total),
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
                if (_tracks.Count > 1)
                    _task.MediaTracksState = BuildTracksState();

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
