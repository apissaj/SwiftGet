namespace SwiftGet.Core;

/// <summary>
/// Kesalahan unduhan dengan kategori yang bisa ditindaklanjuti secara programatik.
/// </summary>
public sealed class DownloadException : Exception
{
    public DownloadException(DownloadErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public DownloadErrorKind Kind { get; }
}

public enum DownloadErrorKind
{
    /// <summary>URL tidak valid atau skema tidak didukung.</summary>
    InvalidUrl,

    /// <summary>Tidak bisa terhubung ke server.</summary>
    ConnectionFailed,

    /// <summary>Server menjawab dengan status HTTP yang tidak bisa dilanjutkan.</summary>
    HttpStatus,

    /// <summary>Konten berubah di server sehingga resume tidak aman.</summary>
    ContentChanged,

    /// <summary>Server mengirim data yang tidak sesuai rentang yang diminta.</summary>
    CorruptResponse,

    /// <summary>Checksum tidak cocok, berkas hasil unduhan rusak.</summary>
    Corruption,

    /// <summary>Ruang disk tidak cukup.</summary>
    DiskFull,

    /// <summary>Izin menulis folder tujuan ditolak.</summary>
    AccessDenied,

    /// <summary>Operasi dibatalkan oleh pengguna.</summary>
    Canceled,
}
