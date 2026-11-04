using SwiftGet.Core.Downloads;

namespace SwiftGet.Tests;

/// <summary>
/// Mengunci perilaku mesin penjadwal: unduhan mana yang waktunya tiba, kapan
/// komputer boleh dimatikan, dan jadwal yang tersimpan di manifest tidak
/// hilang. Waktu selalu diberikan pemanggil sehingga test bebas dari jam mesin.
/// </summary>
public sealed class JadwalEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static (string Id, DateTimeOffset? Jadwal, DownloadState State) Tugas(
        string id, DateTimeOffset? jadwal, DownloadState state) => (id, jadwal, state);

    private static DownloadTask BuatTask(string name) =>
        new(id: Guid.NewGuid().ToString("n"),
            url: new Uri("https://example.test/" + name),
            targetDirectory: Path.GetTempPath(),
            fileName: name);

    // ---- TaskJatuhTempo: batas waktu ----

    [Fact]
    public void TaskJatuhTempo_JadwalTepatSamaDenganNow_Masuk()
    {
        var hasil = JadwalEngine.TaskJatuhTempo(
            new[] { Tugas("a", Now, DownloadState.Queued) }, Now);

        Assert.Equal(new[] { "a" }, hasil);
    }

    [Fact]
    public void TaskJatuhTempo_JadwalDiMasaLalu_JugaMasuk()
    {
        // Mesin penjadwal boleh telat bangun (komputer tidur); jadwal yang
        // sudah lewat tetap harus dijalankan, bukan dibuang.
        var hasil = JadwalEngine.TaskJatuhTempo(
            new[] { Tugas("a", Now.AddHours(-1), DownloadState.Queued) }, Now);

        Assert.Equal(new[] { "a" }, hasil);
    }

    [Fact]
    public void TaskJatuhTempo_JadwalSatuDetikSetelahNow_TidakMasuk()
    {
        var hasil = JadwalEngine.TaskJatuhTempo(
            new[] { Tugas("a", Now.AddSeconds(1), DownloadState.Queued) }, Now);

        Assert.Empty(hasil);
    }

    [Fact]
    public void TaskJatuhTempo_JadwalNull_TidakMasuk()
    {
        var hasil = JadwalEngine.TaskJatuhTempo(
            new[] { Tugas("a", null, DownloadState.Queued) }, Now);

        Assert.Empty(hasil);
    }

    // ---- TaskJatuhTempo: saringan status ----

    [Theory]
    [InlineData(DownloadState.Queued, true)]
    [InlineData(DownloadState.Paused, true)]
    [InlineData(DownloadState.Failed, true)]
    [InlineData(DownloadState.Probing, false)]
    [InlineData(DownloadState.Downloading, false)]
    [InlineData(DownloadState.Completed, false)]
    [InlineData(DownloadState.Canceled, false)]
    public void TaskJatuhTempo_HanyaStatusYangBisaDimulai(DownloadState state, bool harapMasuk)
    {
        var hasil = JadwalEngine.TaskJatuhTempo(
            new[] { Tugas("a", Now, state) }, Now);

        Assert.Equal(harapMasuk, hasil.Count == 1);
    }

    [Fact]
    public void TaskJatuhTempo_Campuran_HanyaYangLayakDanUrutanMasukanDipertahankan()
    {
        var tasks = new[]
        {
            Tugas("berjalan", Now, DownloadState.Downloading), // jadwal tiba tapi sedang bekerja
            Tugas("dua", Now, DownloadState.Queued),
            Tugas("masa-depan", Now.AddSeconds(1), DownloadState.Queued),
            Tugas("satu", Now.AddHours(-2), DownloadState.Paused),
            Tugas("gagal", Now.AddMinutes(-30), DownloadState.Failed),
            Tugas("selesai", Now.AddHours(-1), DownloadState.Completed),
            Tugas("dibatalkan", Now.AddHours(-1), DownloadState.Canceled),
            Tugas("tanpa-jadwal", null, DownloadState.Queued),
        };

        var hasil = JadwalEngine.TaskJatuhTempo(tasks, Now);

        // Urutan mengikuti masukan — pemangku jadwal tidak boleh mengacak baris.
        Assert.Equal(new[] { "dua", "satu", "gagal" }, hasil);
    }

    [Fact]
    public void TaskJatuhTempo_DuaTaskJadwalSama_KeduanyaMasuk()
    {
        var tasks = new[]
        {
            Tugas("a", Now, DownloadState.Queued),
            Tugas("b", Now, DownloadState.Queued),
        };

        var hasil = JadwalEngine.TaskJatuhTempo(tasks, Now);

        Assert.Equal(new[] { "a", "b" }, hasil);
    }

    // ---- HarusMatikanPc ----

    [Fact]
    public void HarusMatikanPc_FlagMati_TetapTidakMematikan()
    {
        var hasil = JadwalEngine.HarusMatikanPc(
            new[] { (DownloadState.Completed, (DateTimeOffset?)null) },
            flagMatikanPc: false, adaYangSelesai: true);

        Assert.False(hasil);
    }

    [Fact]
    public void HarusMatikanPc_TidakAdaYangSelesai_TidakMematikan()
    {
        var hasil = JadwalEngine.HarusMatikanPc(
            new[] { (DownloadState.Completed, (DateTimeOffset?)null) },
            flagMatikanPc: true, adaYangSelesai: false);

        Assert.False(hasil);
    }

    [Fact]
    public void HarusMatikanPc_SemuaSudahTenang_Mematikan()
    {
        var hasil = JadwalEngine.HarusMatikanPc(
            new[]
            {
                (DownloadState.Completed, (DateTimeOffset?)null),
                (DownloadState.Failed, (DateTimeOffset?)null),
                (DownloadState.Canceled, (DateTimeOffset?)null),
            },
            flagMatikanPc: true, adaYangSelesai: true);

        Assert.True(hasil);
    }

    [Fact]
    public void HarusMatikanPc_AdaYangMasihBerjalan_TidakMematikan()
    {
        var hasil = JadwalEngine.HarusMatikanPc(
            new[]
            {
                (DownloadState.Completed, (DateTimeOffset?)null),
                (DownloadState.Downloading, (DateTimeOffset?)null),
            },
            flagMatikanPc: true, adaYangSelesai: true);

        Assert.False(hasil);
    }

    [Fact]
    public void HarusMatikanPc_AdaAntrianTermasukYangMenungguJadwal_TidakMematikan()
    {
        // Queued dengan jadwal di masa depan tetap antrian: mematikan PC
        // sekarang berarti membatalkan jadwal itu diam-diam.
        var tanpaJadwal = JadwalEngine.HarusMatikanPc(
            new[] { (DownloadState.Queued, (DateTimeOffset?)null) },
            flagMatikanPc: true, adaYangSelesai: true);
        var menungguJadwal = JadwalEngine.HarusMatikanPc(
            new[] { (DownloadState.Queued, (DateTimeOffset?)Now.AddHours(3)) },
            flagMatikanPc: true, adaYangSelesai: true);

        Assert.False(tanpaJadwal);
        Assert.False(menungguJadwal);
    }

    [Fact]
    public void HarusMatikanPc_YangDijedaTidakMenghalangi()
    {
        // Keputusan yang dikunci: hanya status aktif (Probing/Downloading) dan
        // Queued yang menahan pemadaman. Paused tidak menghalangi — sengaja,
        // agar pemadaman tidak tertahan selamanya oleh unduhan yang dijeda
        // pengguna sejak lama dan mungkin sudah dilupakan.
        var hasil = JadwalEngine.HarusMatikanPc(
            new[]
            {
                (DownloadState.Completed, (DateTimeOffset?)null),
                (DownloadState.Paused, (DateTimeOffset?)null),
                (DownloadState.Canceled, (DateTimeOffset?)null),
            },
            flagMatikanPc: true, adaYangSelesai: true);

        Assert.True(hasil);
    }

    // ---- Round-trip jadwal lewat manifest ----

    [Fact]
    public void Jadwal_RoundTrip_ManifestPertahankanWaktu()
    {
        var task = BuatTask("terjadwal.bin");
        var jadwal = new DateTimeOffset(2026, 5, 5, 2, 0, 0, TimeSpan.Zero);
        task.SetSchedule(jadwal);

        var kembali = DownloadTask.FromManifest(task.ToManifest());

        Assert.NotNull(kembali.ScheduledStartUtc);
        Assert.Equal(jadwal.UtcTicks, kembali.ScheduledStartUtc!.Value.UtcTicks);
    }

    [Fact]
    public void Jadwal_RoundTrip_TanpaJadwalTetapNull()
    {
        var task = BuatTask("bebas.bin");

        var kembali = DownloadTask.FromManifest(task.ToManifest());

        Assert.Null(task.ScheduledStartUtc);
        Assert.Null(kembali.ScheduledStartUtc);
    }

    [Fact]
    public void SetSchedule_Null_MembatalkanJadwal()
    {
        var task = BuatTask("batal.bin");
        task.SetSchedule(new DateTimeOffset(2026, 5, 5, 2, 0, 0, TimeSpan.Zero));

        task.SetSchedule(null);

        Assert.Null(task.ScheduledStartUtc);
    }
}
