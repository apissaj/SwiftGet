using SwiftGet.Core.Http;
using SwiftGet.Core.Scheduling;
using SwiftGet.Core.Storage;

namespace SwiftGet.Core.Downloads;

/// <summary>
/// Orkestrator satu unduhan: probe, rencana segmen, worker paralel, penyelesaian.
/// <para>
/// Alur:
/// <list type="number">
///   <item>Probe URL untuk panjang konten dan dukungan Range.</item>
///   <item>Rencanakan segmen awal (atau satu segmen bila Range tidak didukung).</item>
///   <item>Kolam worker mengambil segmen dari koordinator: yang terbesar yang
///         belum diklaim. Jumlah worker = batas koneksi, bukan jumlah segmen.</item>
///   <item>Dynamic segmentation: worker yang menganggur memecah segmen aktif
///         terbesar dan mengambil ekornya; segmen kecil yang bersebelahan
///         digabung sebelum diklaim. Rentang yang gagal berkali-kali
///         dikembalikan ke antrean untuk worker lain, bukan dibiarkan gantung.</item>
///   <item>Setelah semua segmen selesai, file sementara dipindahkan menjadi file akhir.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DownloadSession : IDownloadSession
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly DownloadTask _task;
    private readonly HttpRangeClient _http;
    private readonly ConnectionBudget _budget;
    private readonly ManifestStore _store;
    private readonly RetryPolicy _retry;
    private readonly int _maxConnections;
    private readonly Action<DownloadProgress>? _onProgress;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _manifestGate = new(1, 1);

    // Koordinator klaim kerja. Urutan penguncian selalu _workGate lalu gate
    // internal _task, tidak pernah sebaliknya, supaya tidak ada deadlock.
    private readonly object _workGate = new();
    private readonly HashSet<int> _claimed = new();

    /// <summary>Percobaan ulang kumulatif per segmen, melewati batas antar klaim.</summary>
    private readonly Dictionary<int, int> _segmentAttempts = new();
    private volatile bool _workAbandoned;

    private PartFile? _partFile;
    private Task? _runner;
    private volatile bool _paused;
    private volatile bool _canceled;
    private bool _disposed;

    public DownloadSession(
        DownloadTask task,
        HttpRangeClient http,
        ConnectionBudget budget,
        ManifestStore store,
        int maxConnections,
        RetryPolicy? retry = null,
        Action<DownloadProgress>? onProgress = null)
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

    /// <summary>
    /// Jalankan atau lanjutkan unduhan sampai selesai, gagal, atau dihentikan.
    /// Memanggil ulang setelah <see cref="Pause"/> akan melanjutkan dari posisi terakhir.
    /// </summary>
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

    private async System.Threading.Tasks.Task RunCoreAsync(CancellationToken ct)
    {
        try
        {
            if (_task.GetSegments().Count == 0 || _task.TotalLength is null)
            {
                _task.SetState(DownloadState.Probing);
                PublishProgress();

                ProbeResult probe = await _http.ProbeAsync(_task.Url, ct).ConfigureAwait(false);

                ApplyProbe(probe);

                if (!probe.CanSegment && probe.ContentLength is null)
                {
                    // Tanpa panjang konten: unduh sebagai satu aliran buta.
                    _task.SupportsRanges = false;
                }
            }
            else if (!await ValidatorsStillMatchAsync(ct).ConfigureAwait(false))
            {
                throw new DownloadException(DownloadErrorKind.ContentChanged,
                    "Berkas di server berubah sejak unduhan terakhir. Mulai ulang dari awal.");
            }

            await _budget.EnterDownloadAsync(ct).ConfigureAwait(false);
            try
            {
                await DownloadAllSegmentsAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _budget.ExitDownload();
            }

            if (_paused)
            {
                _task.SetState(DownloadState.Paused);
                PublishProgress();
                return;
            }

            await FinalizeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_canceled)
        {
            _task.SetState(DownloadState.Canceled);
            PublishProgress();
        }
        catch (OperationCanceledException)
        {
            _task.SetState(DownloadState.Paused);
            PublishProgress();
        }
        catch (DownloadException ex)
        {
            if (ex.Kind == DownloadErrorKind.Corruption)
            {
                // Segmen yang tersimpan adalah bagian dari berkas yang terbukti
                // salah. Nolkan kemajuan supaya resume mengunduh ulang dari awal,
                // bukan menyusun ulang data korup yang sama terus-menerus.
                _task.ResetSegments();
            }

            _task.SetState(DownloadState.Failed, ex.Message);
            PublishProgress();
        }
        finally
        {
            _partFile?.Dispose();
            _partFile = null;
            await SaveManifestSafeAsync().ConfigureAwait(false);
        }
    }

    private void ApplyProbe(ProbeResult probe)
    {
        _task.FinalUri = probe.FinalUri;
        _task.TotalLength = probe.ContentLength;
        _task.SupportsRanges = probe.CanSegment;
        _task.ETag = probe.ETag;
        _task.LastModifiedRaw = probe.LastModifiedRaw;
        _task.ChecksumAlgorithm = probe.ExpectedChecksumAlgorithm;
        _task.ChecksumValue = probe.ExpectedChecksumValue;

        if (probe.FileNameFromServer && probe.SuggestedFileName is { Length: > 0 } suggested)
        {
            _task.FileName = suggested;
        }

        var segments = probe.CanSegment
            ? SegmentPlanner.Plan(probe.ContentLength!.Value, _maxConnections)
            : new List<Segment> { new(0, 0, probe.ContentLength ?? 0) };

        _task.SetSegments(segments);
    }

    private async Task<bool> ValidatorsStillMatchAsync(CancellationToken ct)
    {
        // Untuk unduhan yang belum pernah jalan, tidak ada validator tersimpan.
        if (_task.ETag is null && _task.LastModifiedRaw is null) return true;

        ProbeResult probe = await _http.ProbeAsync(_task.Url, ct).ConfigureAwait(false);

        if (_task.TotalLength is not null && probe.ContentLength is not null &&
            _task.TotalLength != probe.ContentLength)
        {
            return false;
        }

        if (_task.ETag is not null && probe.ETag is not null &&
            !string.Equals(_task.ETag, probe.ETag, StringComparison.Ordinal))
        {
            return false;
        }

        if (_task.LastModifiedRaw is not null && probe.LastModifiedRaw is not null &&
            !string.Equals(_task.LastModifiedRaw, probe.LastModifiedRaw, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task DownloadAllSegmentsAsync(CancellationToken ct)
    {
        long totalLength = _task.TotalLength ?? 0;
        if (totalLength <= 0)
            throw new DownloadException(DownloadErrorKind.HttpStatus,
                "Server tidak menyatakan panjang berkas; tidak bisa mengunduh dengan aman.");

        _partFile = PartFile.Open(_store.GetPartPath(_task.Id), totalLength);

        _task.SetState(DownloadState.Downloading);
        PublishProgress();

        int unfinished = _task.GetSegments().Count(s => !s.IsComplete);
        if (unfinished == 0)
        {
            await SaveManifestSafeAsync().ConfigureAwait(false);
            return;
        }

        // Jumlah worker = batas koneksi, bukan jumlah segmen: segmen boleh
        // bertambah (pemecahan) dan berkurang (penggabungan) saat berjalan.
        int workerCount = Math.Min(_maxConnections, unfinished);
        var workers = new List<Task>(workerCount);
        for (int i = 0; i < workerCount; i++)
            workers.Add(WorkerLoopAsync(ct));

        await System.Threading.Tasks.Task.WhenAll(workers).ConfigureAwait(false);

        // Worker hanya berhenti bila tidak ada kerja yang bisa diklaim atau
        // dipecah. Sisa yang belum lengkap berarti pekerjaan ditinggalkan
        // (segmen gagal terus-menerus) — jangan diam-diam dianggap selesai.
        IReadOnlyList<Segment> refreshed = _task.GetSegments();
        if (refreshed.Any(s => !s.IsComplete) && !_paused && !_canceled)
        {
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "Masih ada segmen yang belum lengkap setelah semua worker berhenti.");
        }
    }

    /// <summary>
    /// Loop kerja satu worker: klaim segmen terbesar yang belum diklaim,
    /// kerjakan sampai tuntas, lalu klaim berikutnya. Berhenti bila tidak ada
    /// kerja tersisa yang bisa diambil atau dipecah.
    /// </summary>
    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        string host = _task.Url.Host;

        while (!ct.IsCancellationRequested && !_paused)
        {
            int? segmentIndex = ClaimNextWork();
            if (segmentIndex is null) return;

            try
            {
                await ProcessSegmentAsync(segmentIndex.Value, host, ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_workGate) _claimed.Remove(segmentIndex.Value);
            }
        }
    }

    /// <summary>
    /// Ambil kerja berikutnya dari koordinator: gabung segmen kecil bersebelahan
    /// yang belum mulai, klaim segmen terbesar yang belum diklaim, atau — bila
    /// tidak ada yang menganggur — pecah segmen aktif terbesar dan klaim ekornya.
    /// Mengembalikan null bila tidak ada lagi yang bisa dikerjakan.
    /// </summary>
    private int? ClaimNextWork()
    {
        lock (_workGate)
        {
            if (_workAbandoned) return null;

            if (_task.SupportsRanges)
            {
                // Penggabungan: dua segmen bersebelahan yang belum mulai menjadi
                // satu request Range, bukan dua request mikro.
                IReadOnlyList<Segment> before = _task.GetSegments();
                for (int i = 0; i + 1 < before.Count; i++)
                {
                    Segment a = before[i];
                    Segment b = before[i + 1];
                    if (!_claimed.Contains(a.Index) && !_claimed.Contains(b.Index) &&
                        a.BytesDone == 0 && b.BytesDone == 0)
                    {
                        _task.MergeWithNext(a.Index);
                        break; // satu gabung per klaim; snapshot diambil ulang
                    }
                }
            }

            IReadOnlyList<Segment> segments = _task.GetSegments();

            // Work-stealing ala IDM: segmen sisa terbesar dikerjakan lebih dulu.
            Segment? best = null;
            foreach (Segment s in segments)
            {
                if (s.IsComplete || _claimed.Contains(s.Index)) continue;
                if (best is null || s.Remaining > best.Remaining) best = s;
            }

            if (best is not null)
            {
                _claimed.Add(best.Index);
                return best.Index;
            }

            // Tidak ada kerja menganggur, tapi masih ada segmen aktif yang
            // besar: pecah ekornya supaya koneksi ini ikut bekerja.
            if (_task.SupportsRanges)
            {
                Segment? victim = null;
                foreach (Segment s in segments)
                {
                    if (!_claimed.Contains(s.Index) || s.IsComplete) continue;
                    if (s.Remaining >= 2 * SegmentPlanner.MinSegmentSize &&
                        (victim is null || s.Remaining > victim.Remaining)) victim = s;
                }

                if (victim is not null)
                {
                    long splitPoint = victim.End - victim.Remaining / 2;
                    splitPoint -= splitPoint % SegmentPlanner.AlignmentBytes;
                    if (splitPoint >= victim.NextPosition + SegmentPlanner.MinSegmentSize)
                    {
                        int newIndex = _task.SplitFromEnd(victim.Index, splitPoint);
                        _claimed.Add(newIndex);
                        return newIndex;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Kerjakan satu segmen sampai tuntas: ambil izin koneksi, minta rentang
    /// sisa, tulis ke file. Gagal sementara diulang; habis kuota, rentangnya
    /// dikembalikan ke koordinator untuk worker lain.
    /// </summary>
    private async Task ProcessSegmentAsync(int segmentIndex, string host, CancellationToken ct)
    {
        int attempt = 0;

        while (attempt < _retry.MaxAttempts && !ct.IsCancellationRequested && !_paused)
        {
            attempt++;
            bool entered = false;

            try
            {
                await _budget.EnterConnectionAsync(host, ct).ConfigureAwait(false);
                entered = true;

                await DownloadSegmentOnceAsync(segmentIndex, ct).ConfigureAwait(false);
                return; // segmen selesai
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (DownloadException ex) when (ex.Kind == DownloadErrorKind.ContentChanged)
            {
                throw; // perubahan konten tidak layak diulang per segmen
            }
            catch (DownloadException ex) when (RetryPolicy.IsTransient(ex))
            {
                // Rentang dikembalikan ke antrean: worker lain (atau worker ini,
                // lewat klaim berikutnya) meneruskannya. Bila segmen yang sama
                // gagal terus melampaui batas kumulatif, hentikan semuanya.
                lock (_workGate)
                {
                    int total = _segmentAttempts.TryGetValue(segmentIndex, out int n) ? n : 0;
                    _segmentAttempts[segmentIndex] = total + 1;
                    if (total + 1 >= _retry.MaxAttempts * 2) _workAbandoned = true;
                }

                if (!_workAbandoned)
                    await SafeDelayAsync(_retry.GetDelay(attempt), ct).ConfigureAwait(false);
                return;
            }
            catch (DownloadException)
            {
                throw;
            }
            finally
            {
                if (entered) _budget.ExitConnection(host);
            }
        }
    }

    private async Task DownloadSegmentOnceAsync(int segmentIndex, CancellationToken ct)
    {
        Segment segment = _task.GetSegments()[segmentIndex];
        string? range = segment.BuildRangeHeader();
        if (range is null) return;

        long from = segment.NextPosition;
        long toExclusive = segment.End;

        // Bila Range tidak didukung, hanya segmen 0 yang boleh jalan, dan ia mulai dari 0.
        if (!_task.SupportsRanges && segmentIndex != 0)
            throw new DownloadException(DownloadErrorKind.HttpStatus,
                "Server tidak mendukung Range; segmen tambahan tidak bisa dijalankan.");

        using RangedResponse response = await _http
            .GetRangeAsync(_task.Url, from, toExclusive - 1, ct)
            .ConfigureAwait(false);

        if (!response.RangeHonored && !_task.SupportsRanges)
        {
            // Unduhan single-stream sejak awal: tulis sequential dari posisi sekarang.
            await PumpAsync(segmentIndex, response.GetStream(), ct).ConfigureAwait(false);
            return;
        }

        if (!response.RangeHonored)
        {
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "Server mengabaikan Range padahal sebelumnya menyatakan dukungan. " +
                "Unduhan dihentikan agar berkas tidak rusak.");
        }

        await PumpAsync(segmentIndex, response.GetStream(), ct).ConfigureAwait(false);
    }

    /// <summary>Baca aliran respons dan tulis ke file sementara pada offset segmen.</summary>
    private async Task PumpAsync(int segmentIndex, Stream source, CancellationToken ct)
    {
        PartFile part = _partFile ?? throw new InvalidOperationException("File sementara belum dibuka.");

        byte[] buffer = new byte[CopyBufferSize];

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_paused) return;

            Segment segment = _task.GetSegments()[segmentIndex];
            if (segment.IsComplete) return;

            // Jangan pernah membaca melebihi sisa rentang segmen.
            long remaining = segment.Remaining;
            int toRead = (int)Math.Min(buffer.Length, remaining);

            int read;
            try
            {
                read = await source.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new DownloadException(DownloadErrorKind.ConnectionFailed,
                    $"Koneksi terputus saat membaca data: {ex.Message}", ex);
            }

            if (read <= 0) return; // server menutup aliran

            long writeOffset = segment.NextPosition;
            await part.WriteAtAsync(writeOffset, buffer.AsMemory(0, read), ct).ConfigureAwait(false);

            bool segmentDone = _task.Advance(segmentIndex, read);
            PublishProgress();
            await SaveManifestThrottledAsync(ct).ConfigureAwait(false);

            if (segmentDone) return;
        }
    }

    private async Task FinalizeAsync(CancellationToken ct)
    {
        PartFile part = _partFile ?? throw new InvalidOperationException("Tidak ada file sementara.");
        await part.FlushAsync(ct).ConfigureAwait(false);

        part.CommitAs(_task.FinalPath, overwrite: true);
        _partFile = null;

        if (!string.IsNullOrWhiteSpace(_task.ChecksumAlgorithm) && !string.IsNullOrWhiteSpace(_task.ChecksumValue))
        {
            string? calculated;
            try
            {
                calculated = await Storage.Checksum.ComputeAsync(
                    _task.FinalPath, _task.ChecksumAlgorithm, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Berkas tidak bisa dibaca untuk hash (mis. dikunci antivirus
                // sesaat setelah rename). Verifikasi dilewati, bukan memfailkan
                // unduhan yang datanya sudah lengkap.
                calculated = null;
            }

            // Algoritma tidak dikenal juga mengembalikan null: server menawarkan
            // checksum yang tidak bisa dihitung, bukan berkas yang salah.
            if (calculated is not null &&
                !string.Equals(calculated, _task.ChecksumValue, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(_task.FinalPath); }
                catch (IOException) { /* dibiarkan; manifest tetap tercatat korup */ }

                throw new DownloadException(DownloadErrorKind.Corruption,
                    $"Integritas berkas rusak! Hasil unduhan tidak cocok dengan checksum server. " +
                    $"Diharapkan: {_task.ChecksumValue}, dihitung: {calculated}.");
            }
        }

        _task.SetState(DownloadState.Completed);
        PublishProgress();

        // Hapus manifest dan sisa file sementara; file akhir sudah dipindahkan.
        _store.Delete(_task.Id);
    }

    private long _lastManifestWriteTicks;
    private readonly object _manifestWriteGate = new();

    private async Task SaveManifestThrottledAsync(CancellationToken ct)
    {
        long now = Environment.TickCount64;
        lock (_manifestWriteGate)
        {
            // Tulis manifest paling sering tiap 1 detik; progres tetap akurat
            // karena shutdown selalu menyimpan versi terakhir.
            if (now - _lastManifestWriteTicks < 1000) return;
            _lastManifestWriteTicks = now;
        }

        await SaveManifestSafeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Simpan manifest tanpa pernah melempar. Kegagalan penyimpanan tidak boleh
    /// menggagalkan unduhan; manifest terakhir disimpan di akhir sesi.
    /// </summary>
    private async Task SaveManifestSafeAsync(CancellationToken ct = default)
    {
        try
        {
            await _manifestGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_task.State == DownloadState.Completed) return;
                await _store.SaveAsync(_task.ToManifest(), ct).ConfigureAwait(false);
            }
            finally
            {
                _manifestGate.Release();
            }
        }
        catch (OperationCanceledException) { /* penyimpanan terakhir tidak boleh menggagalkan */ }
        catch (IOException) { /* idem */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }

    private void PublishProgress()
    {
        try { _onProgress?.Invoke(_task.Snapshot()); }
        catch { /* callback UI tidak boleh mematikan unduhan */ }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await System.Threading.Tasks.Task.Delay(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* keluar dari loop lewat pengecekan ct */ }
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_runner is { IsCompleted: false })
        {
            _cts.Cancel();
            try { await _runner.ConfigureAwait(false); } catch { /* diabaikan saat dispose */ }
        }

        _partFile?.Dispose();
        _cts.Dispose();
        _manifestGate.Dispose();
    }
}
