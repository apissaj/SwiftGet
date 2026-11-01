using SwiftGet.App.Infrastructure;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji penyimpan riwayat unduhan: round-trip JSON, urutan terbaru di depan,
/// trim ke 500 entri, dan ketahanan terhadap berkas korup atau folder kosong.
/// <para>
/// Setiap test memakai folder sementara sendiri — riwayat asli aplikasi di
/// %LOCALAPPDATA% tidak boleh tersentuh.
/// </para>
/// </summary>
public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir;

    public HistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "swiftget-history-tests", Guid.NewGuid().ToString("N"));
        // Sengaja tidak dibuat di sini: sebagian test menguji bahwa Record
        // membuat folder sendiri, sisanya membuatnya lewat SiapkanFolder().
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string HistoryPath => Path.Combine(_dir, "history.json");

    /// <summary>Folder sementara untuk test yang butuh folder sudah ada.</summary>
    private void SiapkanFolder() => Directory.CreateDirectory(_dir);

    private HistoryStore MakeStore() => new(_dir);

    private static HistoryEntry MakeEntry(
        string fileName,
        long? totalBytes = 1_234_567,
        DateTimeOffset? completedUtc = null,
        string? id = null)
    {
        return new HistoryEntry
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            FileName = fileName,
            Url = "https://example.test/" + fileName,
            FinalPath = Path.Combine(@"C:\Unduhan", fileName),
            TotalBytes = totalBytes,
            Category = FileCategories.LabelOf(fileName),
            CompletedUtc = completedUtc ?? DateTimeOffset.UtcNow,
        };
    }

    // ---- Load: folder kosong / belum ada ----

    [Fact]
    public void Load_FolderKosong_MengembalikanDaftarKosong()
    {
        SiapkanFolder();
        var store = MakeStore();

        var hasil = store.Load();

        Assert.Empty(hasil);
    }

    [Fact]
    public void Load_FolderBelumAda_MengembalikanDaftarKosong()
    {
        // Folder belum dibuat sama sekali: Load tidak boleh melempar.
        var store = MakeStore();

        var hasil = store.Load();

        Assert.Empty(hasil);
    }

    // ---- Record: round-trip satu entri ----

    [Fact]
    public void Record_SatuEntri_RoundTripSemuaField()
    {
        var store = MakeStore();
        var asli = MakeEntry("film.mp4", 7_000_000_000, DateTimeOffset.UtcNow);

        store.Record(asli);

        var hasil = Assert.Single(store.Load());
        Assert.Equal(asli.Id, hasil.Id);
        Assert.Equal(asli.FileName, hasil.FileName);
        Assert.Equal(asli.Url, hasil.Url);
        Assert.Equal(asli.FinalPath, hasil.FinalPath);
        Assert.Equal(asli.TotalBytes, hasil.TotalBytes);
        Assert.Equal(asli.Category, hasil.Category);
        // Presisi waktu tersimpan setingkat Tick ("O" ISO 8601 round-trip).
        Assert.Equal(asli.CompletedUtc.Ticks, hasil.CompletedUtc.Ticks);
        Assert.Equal(asli.CompletedUtc.Offset, hasil.CompletedUtc.Offset);
    }

    [Fact]
    public void Record_TotalBytesNull_RoundTripTetapNull()
    {
        var store = MakeStore();
        var asli = MakeEntry("belum-tahu-size.mkv", totalBytes: null);

        store.Record(asli);

        var hasil = Assert.Single(store.Load());
        Assert.Null(hasil.TotalBytes);
    }

    // ---- Record: urutan terbaru di depan ----

    [Fact]
    public void Record_TigaEntri_TerakhirDicatatDiIndexNol()
    {
        var store = MakeStore();
        var waktu = DateTimeOffset.UtcNow;

        store.Record(MakeEntry("pertama.zip", completedUtc: waktu.AddSeconds(1)));
        store.Record(MakeEntry("kedua.zip", completedUtc: waktu.AddSeconds(2)));
        store.Record(MakeEntry("ketiga.zip", completedUtc: waktu.AddSeconds(3)));

        var hasil = store.Load();
        Assert.Equal(3, hasil.Count);
        Assert.Equal("ketiga.zip", hasil[0].FileName); // terakhir dicatat paling depan
        Assert.Equal("kedua.zip", hasil[1].FileName);
        Assert.Equal("pertama.zip", hasil[2].FileName);
    }

    // ---- Record: trim ke batas 500 entri ----

    [Fact]
    public void Record_LebihDariLimaRatus_TrimKeLimaRatusTerbaru()
    {
        var store = MakeStore();
        var waktu = DateTimeOffset.UtcNow;

        // 505 entri: e-000 dicatat paling awal, e-504 paling akhir.
        for (int i = 0; i < 505; i++)
        {
            store.Record(MakeEntry($"e-{i:D3}.bin", completedUtc: waktu.AddSeconds(i), id: $"id-{i:D3}"));
        }

        var hasil = store.Load();
        Assert.Equal(500, hasil.Count);                      // tepat 500 entri
        Assert.Equal("id-504", hasil[0].Id);                 // entri terbaru ada di depan
        Assert.DoesNotContain(hasil, e => e.Id == "id-000"); // entri pertama dicatat sudah terbuang
        Assert.DoesNotContain(hasil, e => e.Id == "id-004"); // 5 entri terlama (id-004..id-000) terbuang
        Assert.Equal("id-005", hasil[^1].Id);                // yang tertua tersisa adalah id-005
    }

    // ---- Clear ----

    [Fact]
    public void Clear_SetelahRecord_MenghapusFileDanLoadKosong()
    {
        SiapkanFolder();
        var store = MakeStore();
        store.Record(MakeEntry("film.mp4"));
        Assert.True(File.Exists(HistoryPath));

        store.Clear();

        Assert.False(File.Exists(HistoryPath));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Clear_SaatFileTidakAda_TidakMelempar()
    {
        SiapkanFolder();
        var store = MakeStore();

        store.Clear(); // tidak boleh melempar walau history.json belum ada

        Assert.False(File.Exists(HistoryPath));
    }

    // ---- Berkas korup ----

    [Fact]
    public void Load_BerkasKorup_MengembalikanKosongTanpaMelempar()
    {
        SiapkanFolder();
        File.WriteAllText(HistoryPath, "ini bukan json {{{");
        var store = MakeStore();

        var hasil = store.Load();

        Assert.Empty(hasil);
    }

    // ---- Record membuat folder otomatis ----

    [Fact]
    public void Record_FolderBelumAda_FolderDibuatOtomatisDanTersimpan()
    {
        // _dir belum ada; Record harus membuat folder sendiri.
        var store = MakeStore();

        store.Record(MakeEntry("catatan.pdf"));

        Assert.True(Directory.Exists(_dir));
        Assert.True(File.Exists(HistoryPath));
        var hasil = Assert.Single(store.Load());
        Assert.Equal("catatan.pdf", hasil.FileName);
    }
}
