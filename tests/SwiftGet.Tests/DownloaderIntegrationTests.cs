using System.Security.Cryptography;
using SwiftGet.Core;
using SwiftGet.Core.Downloads;
using SwiftGet.Core.Http;
using SwiftGet.Core.Scheduling;
using SwiftGet.Core.Storage;
using SwiftGet.Tests.Fixtures;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji end-to-end engine terhadap server HTTP lokal. Ini inti pembuktian bahwa
/// fitur "unduhan dipecah jadi beberapa bagian" benar-benar bekerja.
/// </summary>
public sealed class DownloaderIntegrationTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private string _workDir = null!;

    public Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _workDir = Path.Combine(Path.GetTempPath(), "swiftget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private string StateDir => Path.Combine(_workDir, "state");
    private string OutDir => Path.Combine(_workDir, "out");

    private static byte[] MakePayload(int length)
    {
        var data = new byte[length];
        // Pola deterministik supaya urutan byte yang salah langsung terdeteksi.
        for (int i = 0; i < length; i++) data[i] = (byte)(i * 31 + (i >> 8));
        return data;
    }

    private DownloadTask NewTask(string fileName = "file.bin")
    {
        Directory.CreateDirectory(OutDir);
        var task = new DownloadTask(Guid.NewGuid().ToString("N"), _server.UrlFor(fileName), OutDir, fileName);
        return task;
    }

    [Fact]
    public async Task Unduh_Paralel_Menghasilkan_File_Identik()
    {
        _server.Content = MakePayload(4 * 1024 * 1024);

        DownloadTask task = NewTask();
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget(new SchedulerOptions { MaxConnectionsPerDownload = 8 });

        await using var session = new DownloadSession(task, http, budget, store, maxConnections: 8);
        await session.RunAsync();

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.True(File.Exists(task.FinalPath), $"File akhir tidak ada: {task.FinalPath}");

        byte[] actual = await File.ReadAllBytesAsync(task.FinalPath);
        Assert.Equal(_server.Content.Length, actual.Length);
        Assert.Equal(_server.Content, actual);

        // Harus benar-benar memakai lebih dari satu koneksi.
        Assert.True(_server.RangeRequestCount > 1,
            $"Diharapkan beberapa permintaan Range, dapat {_server.RangeRequestCount}.");
        Assert.True(task.GetSegments().Count > 1,
            "Diharapkan lebih dari satu segmen untuk file 4 MiB.");
    }

    [Fact]
    public async Task Unduh_Menulis_Setiap_Segmen_Pada_Offset_Benar()
    {
        _server.Content = MakePayload(2 * 1024 * 1024);

        DownloadTask task = NewTask();
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        await using var session = new DownloadSession(task, http, budget, store, maxConnections: 4);
        await session.RunAsync();

        Assert.Equal(DownloadState.Completed, task.State);

        // Bukti terkuat: byte-per-byte sama dengan sumber, yang hanya mungkin
        // bila offset penulisan tiap segmen tepat.
        byte[] actual = await File.ReadAllBytesAsync(task.FinalPath);
        Assert.Equal(_server.Content, actual);

        var segments = task.GetSegments();
        Assert.All(segments, s => Assert.True(s.IsComplete, $"Segmen belum lengkap: {s}"));
        Assert.Equal(_server.Content.Length, segments.Sum(s => s.Length));
    }

    [Fact]
    public async Task Server_Tanpa_Range_Memakai_Satu_Aliran()
    {
        _server.Content = MakePayload(512 * 1024);
        _server.IgnoreRanges = true;

        DownloadTask task = NewTask();
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        await using var session = new DownloadSession(task, http, budget, store, maxConnections: 8);
        await session.RunAsync();

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.False(task.SupportsRanges);

        byte[] actual = await File.ReadAllBytesAsync(task.FinalPath);
        Assert.Equal(_server.Content, actual);
    }

    [Fact]
    public async Task Nama_File_Diambil_Dari_ContentDisposition()
    {
        _server.Content = MakePayload(256 * 1024);
        _server.ContentDisposition = "attachment; filename=\"laporan akhir.pdf\"";

        DownloadTask task = NewTask("ignored.bin");
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();
        budget.GetType();

        await using var session = new DownloadSession(task, http, budget, store, maxConnections: 4);
        await session.RunAsync();

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal("laporan akhir.pdf", task.FileName);
        Assert.True(File.Exists(Path.Combine(OutDir, "laporan akhir.pdf")));
    }

    [Fact]
    public async Task Resume_Melanjutkan_Dari_Posisi_Terakhir()
    {
        _server.Content = MakePayload(3 * 1024 * 1024);

        string id = Guid.NewGuid().ToString("N");
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        // Tahap 1: rencanakan segmen lalu tandai sebagian sudah selesai,
        // meniru unduhan yang berhenti di tengah jalan.
        var task = new DownloadTask(id, _server.UrlFor("partial.bin"), OutDir, "partial.bin");
        Directory.CreateDirectory(OutDir);

        ProbeResult probe = await http.ProbeAsync(task.Url);
        task.TotalLength = probe.ContentLength;
        task.SupportsRanges = probe.CanSegment;
        task.ETag = probe.ETag;

        var segments = SegmentPlanner.Plan(probe.ContentLength!.Value, 4);
        foreach (var s in segments)
        {
            // Anggap tiap segmen sudah separuh jalan.
            typeof(Segment).GetProperty(nameof(Segment.BytesDone))!.SetValue(s, s.Length / 2);
        }
        task.SetSegments(segments);

        // Tulis file sementara berisi paralel separuh tiap segmen seperti kondisi nyata.
        string partPath = store.GetPartPath(id);
        using (var part = PartFile.Open(partPath, probe.ContentLength.Value))
        {
            foreach (var s in segments)
            {
                int half = (int)(s.BytesDone);
                await part.WriteAtAsync(s.Start, _server.Content.AsMemory((int)s.Start, half), default);
            }
        }

        task.SetState(DownloadState.Paused);
        await store.SaveAsync(task.ToManifest());

        int getBefore = _server.GetRequestCount;

        // Tahap 2: pulihkan dari manifest dan lanjutkan.
        DownloadManifest? manifest = await store.LoadAsync(id);
        Assert.NotNull(manifest);

        DownloadTask restored = DownloadTask.FromManifest(manifest!);
        await using var session = new DownloadSession(restored, http, budget, store, maxConnections: 4);

        // Fase resume dipisahkan dari probe "bytes=0-0" (yang juga sah dilakukan
        // unduhan yang sudah selesai). Yang diukur: permintaan DATA saat resume.
        lock (_server.RequestedRanges) _server.RequestedRanges.Clear();

        await session.RunAsync();

        Assert.Equal(DownloadState.Completed, restored.State);

        byte[] actual = await File.ReadAllBytesAsync(restored.FinalPath);
        Assert.Equal(_server.Content, actual);

        // Bukti bahwa itu resume, bukan unduh ulang: setiap permintaan data pada
        // fase resume dimulai dari tengah segmen (bukan 0), karena separuh awal
        // setiap segmen sudah dianggap selesai.
        Assert.True(_server.GetRequestCount > getBefore);

        List<(long From, long To)> resumeRequests;
        lock (_server.RequestedRanges) resumeRequests = _server.RequestedRanges.ToList();

        Assert.NotEmpty(resumeRequests);

        // Probe (1 byte) diabaikan; sisanya adalah permintaan data nyata.
        List<(long From, long To)> dataRequests = resumeRequests
            .Where(r => r.To > r.From)
            .ToList();

        Assert.NotEmpty(dataRequests);
        Assert.All(dataRequests, r =>
            Assert.True(r.From > 0,
                $"Permintaan data mulai dari 0 menandakan unduh ulang: {r}"));
    }

    [Fact]
    public async Task Konten_Berubah_Menolak_Resume()
    {
        _server.Content = MakePayload(1024 * 1024);
        _server.ETag = "\"versi-1\"";

        string id = Guid.NewGuid().ToString("N");
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        Directory.CreateDirectory(OutDir);
        var task = new DownloadTask(id, _server.UrlFor("changed.bin"), OutDir, "changed.bin");

        ProbeResult probe = await http.ProbeAsync(task.Url);
        task.TotalLength = probe.ContentLength;
        task.SupportsRanges = true;
        task.ETag = probe.ETag;
        task.SetSegments(SegmentPlanner.Plan(probe.ContentLength!.Value, 2));

        using (var part = PartFile.Open(store.GetPartPath(id), probe.ContentLength.Value)) { }

        task.SetState(DownloadState.Paused);
        await store.SaveAsync(task.ToManifest());

        // Server mengubah konten: ukuran dan ETag berubah.
        _server.Content = MakePayload(1024 * 1024 + 4096);
        _server.ETag = "\"versi-2\"";

        DownloadTask restored = DownloadTask.FromManifest((await store.LoadAsync(id))!);
        await using var session = new DownloadSession(restored, http, budget, store, maxConnections: 2);
        await session.RunAsync();

        Assert.Equal(DownloadState.Failed, restored.State);
        Assert.False(File.Exists(restored.FinalPath),
            "File akhir tidak boleh dibuat dari data yang sudah tidak valid.");
    }

    [Fact]
    public async Task Http_404_Menghasilkan_Status_Failed()
    {
        _server.NotFound = true;

        DownloadTask task = NewTask();
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        await using var session = new DownloadSession(task, http, budget, store, maxConnections: 4);
        await session.RunAsync();

        Assert.Equal(DownloadState.Failed, task.State);
        Assert.NotNull(task.LastError);
    }

    [Fact]
    public async Task Manifest_Tersimpan_Atomik_Dan_Bisa_Dibaca_Ulang()
    {
        var store = new ManifestStore(StateDir);

        var manifest = new DownloadManifest
        {
            Id = "abc123",
            Url = "http://example.com/x.zip",
            FileName = "x.zip",
            TargetDirectory = OutDir,
            TotalLength = 5000,
            SupportsRanges = true,
            State = DownloadState.Paused,
            Segments = new List<DownloadManifest.SegmentRecord>
            {
                new() { Index = 0, Start = 0, End = 2500, BytesDone = 1000 },
                new() { Index = 1, Start = 2500, End = 5000, BytesDone = 0 },
            },
        };

        await store.SaveAsync(manifest);

        // Tidak boleh ada file .tmp yang tertinggal.
        Assert.Empty(Directory.GetFiles(StateDir, "*.tmp"));

        DownloadManifest? loaded = await store.LoadAsync("abc123");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Segments.Count);
        Assert.Equal(1000, loaded.Segments[0].BytesDone);
    }

    [Fact]
    public async Task Checksum_Sha256_Benar_Menghasilkan_Completed()
    {
        _server.Content = MakePayload(1024 * 1024);
        _server.ChecksumSha256 = Convert.ToHexString(
            SHA256.HashData(_server.Content)).ToLowerInvariant();

        DownloadTask task = NewTask();
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        await using var session = new DownloadSession(task, http, budget, store, maxConnections: 4);
        await session.RunAsync();

        Assert.Equal(DownloadState.Completed, task.State);

        // Bukti byte-per-byte: checksum hanya bisa cocok bila seluruh isi file benar.
        byte[] actual = await File.ReadAllBytesAsync(task.FinalPath);
        Assert.Equal(_server.Content, actual);
    }

    [Fact]
    public async Task Checksum_Sha256_Salah_Menghapus_File_Dan_Menolkan_Segmen()
    {
        _server.Content = MakePayload(1024 * 1024);
        // Checksum yang benar tapi dibalik posisinya agar pasti tidak cocok.
        byte[] digest = SHA256.HashData(_server.Content);
        Array.Reverse(digest);
        _server.ChecksumSha256 = Convert.ToHexString(digest).ToLowerInvariant();

        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(OutDir);
        var task = new DownloadTask(id, _server.UrlFor("corrupt.bin"), OutDir, "corrupt.bin");
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        await using var session = new DownloadSession(task, http, budget, store, maxConnections: 4);
        await session.RunAsync();

        Assert.Equal(DownloadState.Failed, task.State);
        Assert.Contains("Integritas berkas rusak", task.LastError);

        // File final yang terbukti korup tidak boleh tertinggal di disk.
        Assert.False(File.Exists(task.FinalPath), "File final korup tidak boleh ada di disk.");

        // Semua segmen task dinolkan: resume harus mengunduh ulang dari awal.
        Assert.All(task.GetSegments(), s => Assert.Equal(0, s.BytesDone));

        // Manifest yang tersimpan juga harus mencermatkan kondisi tersebut.
        DownloadManifest? manifest = await store.LoadAsync(id);
        Assert.NotNull(manifest);
        Assert.Equal("sha256", manifest!.ChecksumAlgorithm);
        Assert.All(manifest.Segments, s => Assert.Equal(0, s.BytesDone));
    }

    [Fact]
    public async Task Resume_Dengan_Checksum_Terpersist_Tetap_Terverifikasi()
    {
        _server.Content = MakePayload(2 * 1024 * 1024);
        string checksum = Convert.ToHexString(SHA256.HashData(_server.Content)).ToLowerInvariant();

        string id = Guid.NewGuid().ToString("N");
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        // Tahap 1: buat manifest yang sedang berjalan, lengkap dengan checksum
        // dan segmen parsial, seperti unduhan yang ditutup di tengah jalan.
        var task = new DownloadTask(id, _server.UrlFor("resume-cs.bin"), OutDir, "resume-cs.bin");
        Directory.CreateDirectory(OutDir);

        ProbeResult probe = await http.ProbeAsync(task.Url);
        task.TotalLength = probe.ContentLength;
        task.SupportsRanges = probe.CanSegment;
        task.ETag = probe.ETag;
        task.ChecksumAlgorithm = "sha256";
        task.ChecksumValue = checksum;

        var segments = SegmentPlanner.Plan(probe.ContentLength!.Value, 2);
        foreach (var s in segments)
        {
            typeof(Segment).GetProperty(nameof(Segment.BytesDone))!.SetValue(s, s.Length / 2);
        }
        task.SetSegments(segments);

        string partPath = store.GetPartPath(id);
        using (var part = PartFile.Open(partPath, probe.ContentLength.Value))
        {
            foreach (var s in segments)
            {
                int half = (int)(s.BytesDone);
                await part.WriteAtAsync(s.Start, _server.Content.AsMemory((int)s.Start, half), default);
            }
        }

        task.SetState(DownloadState.Paused);
        await store.SaveAsync(task.ToManifest());

        // Tahap 2: pulihkan dari manifest — checksum ikut termuat — lalu lanjutkan.
        DownloadTask restored = DownloadTask.FromManifest((await store.LoadAsync(id))!);
        Assert.Equal("sha256", restored.ChecksumAlgorithm);
        Assert.Equal(checksum, restored.ChecksumValue);

        await using var session = new DownloadSession(restored, http, budget, store, maxConnections: 2);
        await session.RunAsync();

        // Checksum terverifikasi lagi di akhir resume: kalau tidak, unduhan
        // yang diteruskan dari data lama bisa saja lolos tanpa pemeriksaan.
        Assert.Equal(DownloadState.Completed, restored.State);

        byte[] actual = await File.ReadAllBytesAsync(restored.FinalPath);
        Assert.Equal(_server.Content, actual);
    }

    [Fact]
    public async Task Digest_Header_Sha256_Base64_Terverifikasi_EndToEnd()
    {
        _server.Content = MakePayload(512 * 1024);
        // SHA-256 dalam base64, seperti format header Digest (RFC 3230).
        _server.Digest = "sha-256=" + Convert.ToBase64String(SHA256.HashData(_server.Content));

        DownloadTask task = NewTask();
        var store = new ManifestStore(StateDir);
        using var http = new HttpRangeClient();
        using var budget = new ConnectionBudget();

        await using var session = new DownloadSession(task, http, budget, store, maxConnections: 4);
        await session.RunAsync();

        // Jalur lengkap: probe membaca Digest, checksum dipersist ke task,
        // lalu diverifikasi setelah file final selesai.
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal("sha256", task.ChecksumAlgorithm);

        byte[] actual = await File.ReadAllBytesAsync(task.FinalPath);
        Assert.Equal(_server.Content, actual);
    }
}


