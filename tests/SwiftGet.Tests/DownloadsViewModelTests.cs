using System.Windows.Threading;
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
}
