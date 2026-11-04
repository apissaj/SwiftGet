namespace SwiftGet.Core.Downloads;

/// <summary>
/// Logika murni penjadwal unduhan (scheduler): tugas apa yang waktunya tiba,
/// dan kapan komputer boleh dimatikan setelah antrian tuntas. Murni fungsi
/// dengan waktu yang diberikan pemanggil — mudah diuji tanpa tidur nyata.
/// Pemangku jadwal sehari-hari ada di view model (timer 20 detik).
/// </summary>
public static class JadwalEngine
{
    /// <summary>
    /// Id unduhan yang jadwalnya sudah tiba dan layak dimulai sekarang:
    /// terjadwal &lt;= <paramref name="now"/> dan masih menunggu (Queued),
    /// dijeda, atau gagal — ketiganya bisa dilanjutkan lewat jalur mulai.
    /// </summary>
    public static IReadOnlyList<string> TaskJatuhTempo(
        IEnumerable<(string Id, DateTimeOffset? Jadwal, DownloadState State)> tasks,
        DateTimeOffset now)
    {
        var hasil = new List<string>();
        foreach (var (id, jadwal, state) in tasks)
        {
            if (jadwal is null || jadwal > now) continue;
            if (state is DownloadState.Queued or DownloadState.Paused or DownloadState.Failed)
                hasil.Add(id);
        }
        return hasil;
    }

    /// <summary>
    /// Benar bila komputer boleh dimatikan: pengguna meminta, ada unduhan yang
    /// selesai sejak permintaan, dan tidak ada lagi yang berjalan atau
    /// menunggu jadwal berikutnya.
    /// </summary>
    public static bool HarusMatikanPc(
        IEnumerable<(DownloadState State, DateTimeOffset? Jadwal)> tasks,
        bool flagMatikanPc, bool adaYangSelesai)
    {
        if (!flagMatikanPc || !adaYangSelesai) return false;
        foreach (var (state, jadwal) in tasks)
        {
            if (state.IsActive()) return false;
            if (state == DownloadState.Queued) return false; // termasuk yang menunggu jadwal
        }
        return true;
    }
}
