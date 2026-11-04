using System.Windows.Threading;
using SwiftGet.App.Infrastructure;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Downloads;

namespace SwiftGet.Tests;

/// <summary>
/// Mengunci perilaku penyaring dan pengurutan daftar unduhan.
/// <para>
/// Ini bagian yang paling mudah rusak tanpa ketahuan: penyaring yang salah
/// menyembunyikan unduhan yang gagal, atau pengurutan yang diam-diam
/// kehilangan baris. Test di sini memeriksa hasil akhir, bukan cara
/// penerapannya.
/// </para>
/// </summary>
public sealed class DownloadsViewModelTests
{
    private static DownloadTask MakeTask(string name, long? totalLength = null)
    {
        var task = new DownloadTask(
            id: Guid.NewGuid().ToString("n"),
            url: new Uri("https://example.test/" + name),
            targetDirectory: Path.GetTempPath(),
            fileName: name);

        if (totalLength is not null) task.TotalLength = totalLength;
        return task;
    }

    private static DownloadsViewModel MakeViewModel(params DownloadTask[] tasks)
    {
        string stateDir = Path.Combine(Path.GetTempPath(), "sg-test-vm-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(stateDir);
        var manager = new DownloadManager(stateDir);
        var vm = new DownloadsViewModel(manager, Dispatcher.CurrentDispatcher);
        vm.Reload(tasks);
        return vm;
    }

    [Fact]
    public void Reload_MenampilkanSemua()
    {
        var vm = MakeViewModel(MakeTask("a.bin"), MakeTask("b.bin"));

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(2, vm.VisibleItems.Count);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.IsFilteredEmpty);
    }

    [Fact]
    public void FilterActive_MenyembunyikanYangSelesaiDanGagal()
    {
        var running = MakeTask("jalan.bin");
        var done = MakeTask("selesai.bin");
        var failed = MakeTask("gagal.bin");

        var vm = MakeViewModel(running, done, failed);
        
        vm.Items.First(i => i.Id == running.Id).ApplyState(DownloadState.Downloading, null);
        vm.Items.First(i => i.Id == done.Id).ApplyState(DownloadState.Completed, null);
        vm.Items.First(i => i.Id == failed.Id).ApplyState(DownloadState.Failed, "gagal");

        vm.Filter = DownloadFilter.Active;

        Assert.Single(vm.VisibleItems);
        Assert.Equal(running.Id, vm.VisibleItems[0].Id);
        Assert.True(vm.HasFilter);
        Assert.False(vm.IsFilteredEmpty);
    }

    [Fact]
    public void FilterFailed_HanyaYangGagal()
    {
        var ok = MakeTask("ok.bin");
        var bad = MakeTask("bad.bin");

        var vm = MakeViewModel(ok, bad);
        vm.Items.First(i => i.Id == bad.Id).ApplyState(DownloadState.Failed, "putus");

        vm.Filter = DownloadFilter.Failed;

        Assert.Single(vm.VisibleItems);
        Assert.Equal(bad.Id, vm.VisibleItems[0].Id);
    }

    [Fact]
    public void FilterTanpaHasil_MenandaiKeadaanKosongTersaring()
    {
        var vm = MakeViewModel(MakeTask("a.bin"), MakeTask("b.bin"));
        vm.Filter = DownloadFilter.Failed;

        Assert.Empty(vm.VisibleItems);
        Assert.False(vm.IsEmpty);
        Assert.True(vm.IsFilteredEmpty);
    }

    [Fact]
    public void ClearFilter_MengembalikanSemuaBaris()
    {
        var vm = MakeViewModel(MakeTask("a.bin"), MakeTask("b.bin"));
        vm.Filter = DownloadFilter.Failed;

        vm.Filter = DownloadFilter.All;

        Assert.Equal(2, vm.VisibleItems.Count);
        Assert.False(vm.HasFilter);
    }

    [Fact]
    public void SortStatus_MenaruhYangGagalDiatas()
    {
        var done = MakeTask("selesai.bin");
        var failed = MakeTask("gagal.bin");
        var running = MakeTask("jalan.bin");

        var vm = MakeViewModel(done, failed, running);
        vm.Items.First(i => i.Id == done.Id).ApplyState(DownloadState.Completed, null);
        vm.Items.First(i => i.Id == failed.Id).ApplyState(DownloadState.Failed, "putus");
        vm.Items.First(i => i.Id == running.Id).ApplyState(DownloadState.Downloading, null);

        vm.Sort = DownloadSort.Status;

        Assert.Equal(DownloadState.Failed, vm.VisibleItems[0].State);
        Assert.Equal(DownloadState.Downloading, vm.VisibleItems[1].State);
        Assert.Equal(DownloadState.Completed, vm.VisibleItems[2].State);
    }

    [Fact]
    public void SortSize_MenaruhYangTerbesarDiatas()
    {
        var small = MakeTask("kecil.bin", 1_000);
        var large = MakeTask("besar.bin", 9_000_000);
        var medium = MakeTask("sedang.bin", 500_000);

        var vm = MakeViewModel(small, large, medium);
        vm.Sort = DownloadSort.Size;

        Assert.Equal(large.Id, vm.VisibleItems[0].Id);
        Assert.Equal(medium.Id, vm.VisibleItems[1].Id);
        Assert.Equal(small.Id, vm.VisibleItems[2].Id);
    }

    [Fact]
    public void SortSize_UkuranBelumDiketahuiBeradaDibawah()
    {
        var unknown = MakeTask("belum.bin");
        var known = MakeTask("tahu.bin", 2_048);

        var vm = MakeViewModel(unknown, known);
        vm.Sort = DownloadSort.Size;

        Assert.Equal(known.Id, vm.VisibleItems[0].Id);
        Assert.Equal(unknown.Id, vm.VisibleItems[1].Id);
    }

    [Fact]
    public void PerubahanStatus_MenyaringUlangOtomatis()
    {
        var task = MakeTask("a.bin");
        var vm = MakeViewModel(task);
        vm.Filter = DownloadFilter.Failed;

        Assert.Empty(vm.VisibleItems);

        // Baris muncul tanpa pengguna menyentuh penyaring lagi.
        vm.Items[0].ApplyState(DownloadState.Failed, "putus");
        vm.VisibleItems.Clear();
        vm.ApplyFilterAndSort();

        Assert.Single(vm.VisibleItems);
    }

    [Fact]
    public void SortDanFilter_BekerjaBersama()
    {
        var failedSmall = MakeTask("gagal-kecil.bin", 10);
        var failedLarge = MakeTask("gagal-besar.bin", 5_000);
        var completed = MakeTask("selesai.bin", 9_999);

        var vm = MakeViewModel(failedSmall, failedLarge, completed);
        vm.Items.First(i => i.Id == failedSmall.Id).ApplyState(DownloadState.Failed, null);
        vm.Items.First(i => i.Id == failedLarge.Id).ApplyState(DownloadState.Failed, null);
        vm.Items.First(i => i.Id == completed.Id).ApplyState(DownloadState.Completed, null);

        vm.Filter = DownloadFilter.Failed;
        vm.Sort = DownloadSort.Size;

        Assert.Equal(2, vm.VisibleItems.Count);
        Assert.Equal(failedLarge.Id, vm.VisibleItems[0].Id);
    }

    // ---- Kategori sebagai penyaring ----

    /// <summary>Pilihan kategori Video di dropdown, dipakai beberapa test.</summary>
    private static DownloadsViewModel.CategoryOption OpsiKategori(DownloadsViewModel vm, DownloadCategory kategori) =>
        vm.CategoryOptions.First(o => o.Value == kategori);

    [Fact]
    public void CategoryOptions_DelapanPilihan_PertamaSemuaKategori()
    {
        var vm = MakeViewModel(MakeTask("a.bin"));

        Assert.Equal(8, vm.CategoryOptions.Count);
        Assert.Null(vm.CategoryOptions[0].Value);
        Assert.Equal("Semua kategori", vm.CategoryOptions[0].Label);

        // Pilihan bawaan adalah "Semua kategori", bukan kategori pertama.
        Assert.Equal(vm.CategoryOptions[0], vm.SelectedCategoryOption);

        // Tujuh pilihan sisanya mencakup tiap kategori tepat sekali, dengan
        // label dari taksonomi yang sama dipakai panel periksa.
        Assert.Equal(7, vm.CategoryOptions.Count(o => o.Value is not null));
        Assert.Equal(Enum.GetValues<DownloadCategory>().Length,
            vm.CategoryOptions.Where(o => o.Value is not null)
                .Select(o => o.Value!.Value).Distinct().Count());
        foreach (var opsi in vm.CategoryOptions.Skip(1))
            Assert.Equal(FileCategories.Label(opsi.Value!.Value), opsi.Label);
    }

    [Fact]
    public void SelectedCategory_MenyaringBerdasarkanKlasifikasiNama()
    {
        var video = MakeTask("film.mp4");
        var audio = MakeTask("lagu.mp3");
        var arsip = MakeTask("paket.zip");

        var vm = MakeViewModel(video, audio, arsip);
        vm.SelectedCategoryOption = OpsiKategori(vm, DownloadCategory.Video);

        Assert.Single(vm.VisibleItems);
        Assert.Equal(video.Id, vm.VisibleItems[0].Id);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.IsFilteredEmpty);

        // Kembali ke "Semua kategori" memunculkan semuanya lagi.
        vm.SelectedCategoryOption = vm.CategoryOptions[0];
        Assert.Equal(3, vm.VisibleItems.Count);
    }

    [Fact]
    public void SelectedCategory_TanpaHasil_IsFilteredEmptyDanKembaliNormal()
    {
        var vm = MakeViewModel(MakeTask("a.bin"), MakeTask("b.bin"));
        vm.SelectedCategoryOption = OpsiKategori(vm, DownloadCategory.Video);

        Assert.Empty(vm.VisibleItems);
        Assert.False(vm.IsEmpty);        // daftar sebenarnya berisi
        Assert.True(vm.IsFilteredEmpty); // hanya penyaring yang mengosongkannya

        vm.SelectedCategoryOption = vm.CategoryOptions[0];
        Assert.Equal(2, vm.VisibleItems.Count);
        Assert.False(vm.IsFilteredEmpty);
    }

    [Fact]
    public void KategoriDanStatus_HanyaYangLolosKeduanya()
    {
        var arsipAktif = MakeTask("paket-aktif.zip");
        var arsipSelesai = MakeTask("paket-selesai.zip");
        var videoAktif = MakeTask("film-aktif.mp4");

        var vm = MakeViewModel(arsipAktif, arsipSelesai, videoAktif);
        vm.Items.First(i => i.Id == arsipAktif.Id).ApplyState(DownloadState.Downloading, null);
        vm.Items.First(i => i.Id == arsipSelesai.Id).ApplyState(DownloadState.Completed, null);
        vm.Items.First(i => i.Id == videoAktif.Id).ApplyState(DownloadState.Downloading, null);

        vm.Filter = DownloadFilter.Active;
        vm.SelectedCategoryOption = OpsiKategori(vm, DownloadCategory.Arsip);

        Assert.Single(vm.VisibleItems);
        Assert.Equal(arsipAktif.Id, vm.VisibleItems[0].Id);

        // Urutan pengaturan tidak boleh mengubah hasil: kategori dulu, baru status.
        vm.SelectedCategoryOption = vm.CategoryOptions[0];
        vm.SelectedCategoryOption = OpsiKategori(vm, DownloadCategory.Arsip);
        vm.Filter = DownloadFilter.All;
        vm.Filter = DownloadFilter.Active;

        Assert.Single(vm.VisibleItems);
        Assert.Equal(arsipAktif.Id, vm.VisibleItems[0].Id);
    }

    [Fact]
    public void NamaBerkasBerubah_KlasifikasiMengikutiNamaBaru()
    {
        var naik = MakeTask("berkas-tanpa-nama"); // awalnya Lainnya
        var turun = MakeTask("klip.mov");         // awalnya Video

        var vm = MakeViewModel(naik, turun);
        var itemNaik = vm.Items.First(i => i.Id == naik.Id);
        var itemTurun = vm.Items.First(i => i.Id == turun.Id);

        vm.SelectedCategoryOption = OpsiKategori(vm, DownloadCategory.Video);

        Assert.Single(vm.VisibleItems);
        Assert.Equal(turun.Id, vm.VisibleItems[0].Id);

        // Probe selesai: server memberi nama akhir lewat Content-Disposition,
        // maka klasifikasi harus mengikuti nama baru, bukan nama lama.
        naik.FileName = "film-akhir.mp4";
        turun.FileName = "berkas-tanpa-ekstensi";
        itemNaik.RefreshFromTask();
        itemTurun.RefreshFromTask();
        vm.ApplyFilterAndSort();

        Assert.Single(vm.VisibleItems);
        Assert.Equal(naik.Id, vm.VisibleItems[0].Id);
        Assert.Equal("film-akhir.mp4", itemNaik.FileName);
        Assert.Equal("Video", itemNaik.CategoryText);
        Assert.Equal("Lainnya", itemTurun.CategoryText);
    }

    // ---- Penanda jadwal di baris ----

    [Fact]
    public void ScheduleText_Terjadwal_MenampilkanPenandaMenunggu()
    {
        var task = MakeTask("terjadwal.bin");
        var vm = MakeViewModel(task);

        task.SetSchedule(new DateTimeOffset(2026, 5, 5, 2, 0, 0, TimeSpan.Zero));
        vm.Items[0].RefreshFromTask();

        // Jam pada teks mengikuti zona mesin; yang dikunci hanya penandanya.
        Assert.Contains("Menunggu jadwal", vm.Items[0].ScheduleText);
    }

    [Fact]
    public void ScheduleText_TanpaJadwalNull_DanKembaliNullSetelahDibatalkan()
    {
        var task = MakeTask("bebas.bin");
        var vm = MakeViewModel(task);

        Assert.Null(vm.Items[0].ScheduleText);

        task.SetSchedule(new DateTimeOffset(2026, 5, 5, 2, 0, 0, TimeSpan.Zero));
        vm.Items[0].RefreshFromTask();
        Assert.NotNull(vm.Items[0].ScheduleText);

        task.SetSchedule(null);
        vm.Items[0].RefreshFromTask();

        Assert.Null(vm.Items[0].ScheduleText);
    }
}
