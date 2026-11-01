namespace SwiftGet.Core.Downloads;

/// <summary>
/// Siklus hidup satu unduhan. Transisi yang sah dijaga oleh <see cref="DownloadTask"/>.
/// </summary>
public enum DownloadState
{
    /// <summary>Baru dibuat, belum menyentuh jaringan.</summary>
    Queued,

    /// <summary>Sedang memeriksa URL (HEAD / Range probe) dan menyiapkan segmen.</summary>
    Probing,

    /// <summary>Ada worker segmen yang aktif menulis data.</summary>
    Downloading,

    /// <summary>Dihentikan oleh pengguna; manifest tetap ada sehingga bisa dilanjutkan.</summary>
    Paused,

    /// <summary>Semua byte sudah diterima dan file akhir sudah diselesaikan.</summary>
    Completed,

    /// <summary>Berhenti karena kesalahan yang tidak bisa dipulihkan otomatis.</summary>
    Failed,

    /// <summary>Dibatalkan pengguna; manifest dan file sementara akan dihapus.</summary>
    Canceled,
}

public static class DownloadStateExtensions
{
    /// <summary>Status akhir: tidak ada lagi pekerjaan yang akan dijalankan otomatis.</summary>
    public static bool IsTerminal(this DownloadState state) =>
        state is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled;

    /// <summary>Status yang boleh dilanjutkan kembali.</summary>
    public static bool IsResumable(this DownloadState state) =>
        state is DownloadState.Paused or DownloadState.Failed;

    public static bool IsActive(this DownloadState state) =>
        state is DownloadState.Probing or DownloadState.Downloading;
}
