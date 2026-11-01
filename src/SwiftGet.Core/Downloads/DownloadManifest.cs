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

    /// <summary>Folder tujuan akhir.</summary>
    public string TargetDirectory { get; set; } = "";

    public long? TotalLength { get; set; }

    public bool SupportsRanges { get; set; }

    public string? ETag { get; set; }

    public string? LastModifiedRaw { get; set; }

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
