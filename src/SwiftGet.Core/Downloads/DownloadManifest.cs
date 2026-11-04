namespace SwiftGet.Core.Downloads;

/// <summary>
/// Data yang dipersistensikan ke disk supaya unduhan bisa dilanjutkan setelah
/// aplikasi ditutup atau crash. Ditulis secara atomik (tmp + rename).
/// <para>
/// Versi skema disimpan eksplisit agar perubahan format di masa depan bisa
/// dideteksi dan dimigrasikan, bukan ditebak.
/// </para>
/// </summary>
public sealed class DownloadManifest
{
    /// <summary>Versi skema manifest. Naikkan bila ada perubahan yang tidak kompatibel.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>URL asli yang diminta pengguna.</summary>
    public string Url { get; set; } = "";

    /// <summary>URL setelah redirect terakhir yang diamati.</summary>
    public string? FinalUrl { get; set; }

    public string FileName { get; set; } = "";

    /// <summary>Folder tujuan akhir (bisa subfolder kategori).</summary>
    public string TargetDirectory { get; set; }

    /// <summary>Folder dasar yang diminta pengguna.</summary>
    public string BaseDirectory { get; set; } = "";

    public long? TotalLength { get; set; }

    public bool SupportsRanges { get; set; }

    public string? ETag { get; set; }

    public string? LastModifiedRaw { get; set; }

    public string? ChecksumAlgorithm { get; set; }

    public string? ChecksumValue { get; set; }

    /// <summary>Unduhan playlist media (HLS/DASH) yang digabung dari segmen.</summary>
    public bool IsMedia { get; set; }

    /// <summary>Index segmen media berikutnya yang belum tertulis (resume).</summary>
    public int? MediaResumeIndex { get; set; }

    /// <summary>Jumlah segmen saat rencana dibuat; berubah berarti konten berubah.</summary>
    public int? MediaSegmentCount { get; set; }

    /// <summary>Halaman asal tautan (bookmarklet), dikirim ulang saat resume.</summary>
    public string? Referer { get; set; }

    /// <summary>Cookie halaman asal (ekstensi browser), dikirim ulang saat resume.</summary>
    public string? Cookies { get; set; }

    /// <summary>Header Authorization Basic preemptive (dari userinfo URL).</summary>
    public string? BasicAuth { get; set; }

    /// <summary>Waktu mulai otomatis (scheduler); null berarti tanpa jadwal.</summary>
    public DateTimeOffset? ScheduledStartUtc { get; set; }

    /// <summary>
    /// Keadaan resume unduhan multi-jalur DASH:
    /// <c>resume:jumlah,resume:jumlah</c> untuk jalur video lalu audio.
    /// </summary>
    public string? MediaTracksState { get; set; }

    public string? ContentType { get; set; }

    public DownloadState State { get; set; } = DownloadState.Queued;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<SegmentRecord> Segments { get; set; } = new();

    /// <summary>Pesan kesalahan terakhir, bila ada.</summary>
    public string? LastError { get; set; }

    public sealed class SegmentRecord
    {
        public int Index { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public long BytesDone { get; set; }
    }
}
