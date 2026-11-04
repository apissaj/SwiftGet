using SwiftGet.Core.Storage;

namespace SwiftGet.Core.Downloads;

/// <summary>
/// Satu pekerjaan unduhan beserta state-nya. Kelas ini adalah tempat
/// kebenaran untuk progres: segmen, status, dan manifest selalu konsisten satu
/// sama lain dan diubah di bawah satu lock.
/// <para>
/// Worker segmen <b>tidak</b> memutasi <see cref="Segment"/> secara langsung;
/// mereka memanggil <see cref="Advance"/> agar semua perubahan tercatat.
/// </para>
/// </summary>
public sealed class DownloadTask
{
    private readonly object _gate = new();
    private readonly List<Segment> _segments = new();
    private readonly SpeedMeter _speed = new();
    private DownloadState _state = DownloadState.Queued;
    private string? _lastError;

    public DownloadTask(string id, Uri url, string targetDirectory, string? fileName = null)
    {
        Id = id;
        Url = url;
        TargetDirectory = targetDirectory;
        BaseDirectory = targetDirectory;
        FileName = fileName ?? GuessFileName(url);
    }

    public string Id { get; }

    public Uri Url { get; internal set; }

    public string TargetDirectory { get; internal set; }

    public string FileName { get; internal set; }

    /// <summary>Folder dasar yang diminta pengguna; TargetDirectory bisa
    /// jadi subfolder kategori di bawahnya.</summary>
    public string BaseDirectory { get; internal set; }

    public long? TotalLength { get; internal set; }

    public bool SupportsRanges { get; internal set; }

    public string? ETag { get; internal set; }

    public string? LastModifiedRaw { get; internal set; }

    public Uri? FinalUri { get; internal set; }

    public string? ChecksumAlgorithm { get; internal set; }

    public string? ChecksumValue { get; internal set; }

    /// <summary>True bila unduhan ini playlist media (HLS/DASH) yang digabung
    /// dari segmen, bukan berkas tunggal dengan Range.</summary>
    public bool IsMedia { get; internal set; }

    /// <summary>Index segmen media berikutnya yang BELUM tertulis; kunci resume.</summary>
    public int? MediaResumeIndex { get; internal set; }

    /// <summary>Jumlah segmen saat rencana media dibuat; playlist yang berubah
    /// jumlah segmennya dianggap beda konten dan unduhan mulai dari nol.</summary>
    public int? MediaSegmentCount { get; internal set; }

    /// <summary>
    /// Halaman asal tautan (dari bookmarklet). Server tertentu menolak
    /// permintaan tanpa asal yang dikenal; resume memakai nilai yang sama.
    /// </summary>
    public string? Referer { get; internal set; }

    /// <summary>
    /// Cookie halaman asal (dari ekstensi browser) untuk server yang
    /// mewajibkan sesi; resume memakai nilai yang sama.
    /// </summary>
    public string? Cookies { get; internal set; }

    /// <summary>
    /// Header Authorization preemptive (Basic) dari kredensial di URL
    /// (http://pengguna:sandi@host/berkas). Ikut tersimpan di manifest —
    /// berarti manifest berisi kredensial; itu konsekuensi yang didokumentasikan.
    /// </summary>
    public string? BasicAuth { get; internal set; }

    /// <summary>
    /// Waktu unduhan otomatis dimulai (scheduler). Null berarti tanpa jadwal;
    /// pemangku jadwal di view model yang memantau nilai ini.
    /// </summary>
    public DateTimeOffset? ScheduledStartUtc { get; private set; }

    /// <summary>Atur atau batalkan jadwal (null = batalkan).</summary>
    public void SetSchedule(DateTimeOffset? waktuUtc) => ScheduledStartUtc = waktuUtc;

    /// <summary>
    /// Keadaan resume unduhan multi-jalur DASH:
    /// <c>resume:jumlah,resume:jumlah</c> untuk jalur video lalu audio.
    /// </summary>
    public string? MediaTracksState { get; internal set; }

    public DownloadState State
    {
        get { lock (_gate) return _state; }
    }

    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>Path file akhir.</summary>
    public string FinalPath => Path.Combine(TargetDirectory, FileName);

    /// <summary>Salinan segmen; aman dibaca dari thread lain.</summary>
    public IReadOnlyList<Segment> GetSegments()
    {
        lock (_gate) return _segments.ToArray();
    }

    internal void SetSegments(IEnumerable<Segment> segments)
    {
        lock (_gate)
        {
            _segments.Clear();
            _segments.AddRange(segments);
        }
    }

    /// <summary>
    /// Nolkan kemajuan semua segmen. Dipanggil setelah hasil unduhan dinyatakan
    /// korup oleh verifikasi checksum: segmen yang tersimpan di manifest adalah
    /// bagian dari berkas yang terbukti salah, jadi resume harus mengunduh ulang
    /// dari nol, bukan menyusun ulang data yang sama.
    /// </summary>
    internal void ResetSegments()
    {
        lock (_gate)
        {
            foreach (Segment s in _segments) s.BytesDone = 0;
        }
    }

    /// <summary>
    /// Pecah segmen dari ujung akhirnya: <paramref name="splitPoint"/> menjadi
    /// End baru segmen lama, dan rentang <c>[splitPoint, EndLama)</c> menjadi
    /// segmen baru yang belum diunduh.
    /// <para>
    /// Hanya End yang berubah; Start dan BytesDone segmen lama tetap, jadi
    /// worker pemiliknya otomatis berhenti di batas baru pada iterasi buffer
    /// berikutnya. Data yang sudah tertulis tidak tersentuh karena setiap
    /// segmen menulis tepat di offset finalnya. Segmen baru diletakkan di ujung
    /// daftar dengan index baru — index segmen lama tidak pernah bergeser.
    /// </para>
    /// </summary>
    /// <returns>Index segmen baru hasil pemecahan.</returns>
    internal int SplitFromEnd(int segmentIndex, long splitPoint)
    {
        lock (_gate)
        {
            Segment victim = _segments[segmentIndex];
            if (splitPoint <= victim.Start || splitPoint >= victim.End)
                throw new ArgumentOutOfRangeException(nameof(splitPoint),
                    $"Titik potong {splitPoint} harus di dalam rentang segmen [{victim.Start}-{victim.End}).");
            if (victim.NextPosition > splitPoint)
                throw new InvalidOperationException(
                    $"Titik potong {splitPoint} berada di area yang sudah tertulis ({victim.NextPosition}).");

            int newIndex = _segments.Count;
            var extra = new Segment(newIndex, splitPoint, victim.End);
            victim.TruncateTo(splitPoint);
            _segments.Add(extra);
            return newIndex;
        }
    }

    /// <summary>
    /// Gabungkan segmen <c>segmentIndex</c> dengan tetangga berikutnya. Keduanya
    /// harus belum mulai diunduh (BytesDone == 0). Tetangga menjadi segmen
    /// zero-width yang dianggap selesai — barisnya dipertahankan supaya index
    /// segmen lain tidak bergeser, dan tidak dipakai lagi oleh siapa pun.
    /// </summary>
    internal bool MergeWithNext(int segmentIndex)
    {
        lock (_gate)
        {
            if (segmentIndex + 1 >= _segments.Count) return false;

            Segment first = _segments[segmentIndex];
            Segment next = _segments[segmentIndex + 1];
            if (first.BytesDone != 0 || next.BytesDone != 0) return false;
            if (next.Start != first.End) return false;

            first.ExtendTo(next.End);
            next.TruncateTo(next.Start); // zero-width: IsComplete, tidak akan diklaim
            return true;
        }
    }

    internal void SetState(DownloadState next, string? error = null)
    {
        lock (_gate)
        {
            _state = next;
            if (error is not null) _lastError = error;
            if (next == DownloadState.Completed || next == DownloadState.Canceled)
                _lastError = next == DownloadState.Canceled ? _lastError : null;
        }
    }

    /// <summary>
    /// Catat kemajuan satu segmen. Mengembalikan true bila segmen itu menjadi
    /// selesai tepat pada pemanggilan ini.
    /// </summary>
    internal bool Advance(int segmentIndex, long bytesWritten, long expectedOffsetAfterWrite)
    {
        lock (_gate)
        {
            Segment target = _segments[segmentIndex];

            // Invarian: bytes harus bertambah tepat sebesar yang dilaporkan.
            if (expectedOffsetAfterWrite != target.Start + bytesWritten)
                throw new InvalidOperationException(
                    $"Progres segmen {segmentIndex} tidak konsisten: " +
                    $"expected {expectedOffsetAfterWrite}, got {target.Start + bytesWritten}.");

            target.BytesDone = bytesWritten;
            _speed.Record(1);

            return target.IsComplete;
        }
    }

    internal bool Advance(int segmentIndex, long deltaBytes)
    {
        lock (_gate)
        {
            Segment target = _segments[segmentIndex];
            long next = Math.Min(target.Length, target.BytesDone + deltaBytes);
            target.BytesDone = next;
            _speed.Record(deltaBytes);
            return target.IsComplete;
        }
    }

    /// <summary>Ambil snapshot progres untuk UI. Murah dan tidak memblokir lama.</summary>
    public DownloadProgress Snapshot()
    {
        lock (_gate)
        {
            long done = 0;
            int completed = 0;
            foreach (var s in _segments)
            {
                done += s.BytesDone;
                if (s.IsComplete) completed++;
            }

            return new DownloadProgress
            {
                Id = Id,
                State = _state,
                BytesDownloaded = done,
                TotalBytes = TotalLength,
                BytesPerSecond = _speed.GetRate(),
                CompletedSegments = completed,
                TotalSegments = _segments.Count,
            };
        }
    }

    /// <summary>Bangun manifest yang siap disimpan.</summary>
    public DownloadManifest ToManifest()
    {
        lock (_gate)
        {
            return new DownloadManifest
            {
                Version = DownloadManifest.CurrentVersion,
                Id = Id,
                Url = Url.ToString(),
                FinalUrl = FinalUri?.ToString(),
                FileName = FileName,
                TargetDirectory = TargetDirectory,
                BaseDirectory = BaseDirectory,
                TotalLength = TotalLength,
                SupportsRanges = SupportsRanges,
                ETag = ETag,
                LastModifiedRaw = LastModifiedRaw,
                ChecksumAlgorithm = ChecksumAlgorithm,
                ChecksumValue = ChecksumValue,
                IsMedia = IsMedia,
                MediaResumeIndex = MediaResumeIndex,
                MediaSegmentCount = MediaSegmentCount,
                Referer = Referer,
                Cookies = Cookies,
                BasicAuth = BasicAuth,
                ScheduledStartUtc = ScheduledStartUtc,
                MediaTracksState = MediaTracksState,
                State = _state,
                LastError = _lastError,
                Segments = _segments.Select(s => new DownloadManifest.SegmentRecord
                {
                    Index = s.Index,
                    Start = s.Start,
                    End = s.End,
                    BytesDone = s.BytesDone,
                }).ToList(),
            };
        }
    }

    /// <summary>Bangun task dari manifest yang tersimpan (untuk resume).</summary>
    public static DownloadTask FromManifest(DownloadManifest manifest)
    {
        var task = new DownloadTask(
            manifest.Id,
            new Uri(manifest.Url),
            manifest.TargetDirectory,
            manifest.FileName)
        {
            BaseDirectory = manifest.BaseDirectory,
            TotalLength = manifest.TotalLength,
            SupportsRanges = manifest.SupportsRanges,
            ETag = manifest.ETag,
            LastModifiedRaw = manifest.LastModifiedRaw,
            FinalUri = manifest.FinalUrl is null ? null : new Uri(manifest.FinalUrl),
            ChecksumAlgorithm = manifest.ChecksumAlgorithm,
            ChecksumValue = manifest.ChecksumValue,
            IsMedia = manifest.IsMedia,
            MediaResumeIndex = manifest.MediaResumeIndex,
            MediaSegmentCount = manifest.MediaSegmentCount,
            Referer = manifest.Referer,
            Cookies = manifest.Cookies,
            BasicAuth = manifest.BasicAuth,
            ScheduledStartUtc = manifest.ScheduledStartUtc,
            MediaTracksState = manifest.MediaTracksState,
        };

        task._lastError = manifest.LastError;

        var segments = manifest.Segments
            .OrderBy(r => r.Index)
            .Select(r => new Segment(r.Index, r.Start, r.End) { BytesDone = r.BytesDone })
            .ToList();

        task.SetSegments(segments);
        task.SetState(manifest.State == DownloadState.Downloading
            ? DownloadState.Paused   // proses mati saat unduh -> dianggap paused
            : manifest.State);

        return task;
    }

    /// <summary>Tebak nama file dari URL bila server tidak menyebutkannya.</summary>
    public static string GuessFileName(Uri url)
    {
        string path = url.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path[(slash + 1)..] : path;

        name = Uri.UnescapeDataString(name);
        return string.IsNullOrWhiteSpace(name) ? "download" : Http.HeaderParsing.Sanitize(name);
    }
}

/// <summary>
/// Pengukur kecepatan dengan jendela geser sederhana. Satu instance per task
/// sehingga angka kecepatan tidak tercampur antar unduhan.
/// </summary>
internal sealed class SpeedMeter
{
    private const int WindowSize = 20; // sampel
    private readonly Queue<(long ticks, long bytes)> _samples = new();
    private long _pendingBytes;

    public void Record(long deltaBytes)
    {
        if (deltaBytes <= 0) return;

        _pendingBytes += deltaBytes;
        long now = Environment.TickCount64;

        if (_samples.Count == 0 || now - _samples.Last().ticks >= 250)
        {
            _samples.Enqueue((now, _pendingBytes));
            _pendingBytes = 0;
            while (_samples.Count > WindowSize) _samples.Dequeue();
        }
    }

    public double GetRate()
    {
        if (_samples.Count < 2) return 0;

        var first = _samples.First();
        var last = _samples.Last();

        long elapsedMs = last.ticks - first.ticks;
        if (elapsedMs <= 0) return 0;

        long total = 0;
        foreach (var (_, bytes) in _samples) total += bytes;

        return total * 1000.0 / elapsedMs;
    }
}
