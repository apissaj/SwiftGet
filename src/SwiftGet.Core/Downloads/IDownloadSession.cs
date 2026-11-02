namespace SwiftGet.Core.Downloads;

/// <summary>
/// Satu unduhan yang bisa dijalankan, dijeda, dan dibatalkan. Ada dua
/// implementasi: <see cref="DownloadSession"/> untuk berkas tunggal dengan
/// Range, dan <see cref="Media.MediaDownloadSession"/> untuk playlist
/// HLS/DASH yang digabung dari segmen.
/// </summary>
public interface IDownloadSession : IAsyncDisposable
{
    DownloadTask Task { get; }

    /// <summary>Jalankan atau lanjutkan sampai selesai, gagal, atau dihentikan.</summary>
    System.Threading.Tasks.Task RunAsync(CancellationToken externalToken = default);

    /// <summary>Hentikan sementara; progres yang sudah tertulis tetap disimpan.</summary>
    void Pause();

    /// <summary>Batalkan dan hapus state supaya unduhan ini hilang sepenuhnya.</summary>
    void Cancel();
}
