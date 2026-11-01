using SwiftGet.Core.Downloads;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji unit operasi segmen dinamis pada <see cref="DownloadTask"/>:
/// pemecahan dari ujung akhir, penggabungan tetangga, dan round-trip manifest.
/// Invarian utama: cakupan [0, total) tetap utuh setelah operasi apa pun.
/// </summary>
public sealed class DownloadTaskSegmentTests
{
    private const long MiB = 1024 * 1024;

    private static DownloadTask BuatTask(string id, long totalLength, IReadOnlyList<Segment> segments)
    {
        var task = new DownloadTask(id, new Uri($"http://test/{id}.bin"), Path.GetTempPath())
        {
            TotalLength = totalLength,
            SupportsRanges = true,
        };
        task.SetSegments(segments);
        return task;
    }

    /// <summary>
    /// Validator cakupan yang ditulis sendiri: segmen zero-width (hasil merge)
    /// diabaikan, sisanya harus menyusun [0, total) tanpa celah dan tanpa
    /// tumpang tindih, terurut menurut Start.
    /// </summary>
    private static void ValidasiCakupan(IReadOnlyList<Segment> segments, long total)
    {
        var aktif = segments.Where(s => s.Length > 0).OrderBy(s => s.Start).ToList();

        long cursor = 0;
        foreach (Segment s in aktif)
        {
            Assert.True(s.Start == cursor,
                $"Celah/tumpang tindih di {s.Start}: diharapkan mulai {cursor}. Segmen: {s}");
            Assert.True(s.BytesDone <= s.Length,
                $"BytesDone melebihi Length pada {s}");
            cursor = s.End;
        }

        Assert.Equal(total, cursor);
    }

    // ---------- SplitFromEnd ----------

    [Fact]
    public void SplitFromEnd_Cakupan_Tetap_Penuh_Tanpa_Celah()
    {
        long total = 1 * MiB;
        DownloadTask task = BuatTask("split-cakupan", total, SegmentPlanner.Plan(total, 4));

        Segment korban = task.GetSegments()[0];
        long endAsal = korban.End;
        long splitPoint = korban.Start + korban.Length / 2;

        int indexBaru = task.SplitFromEnd(0, splitPoint);

        ValidasiCakupan(task.GetSegments(), total);
        Assert.Equal(total, task.GetSegments().Sum(s => s.Length));

        Segment lama = task.GetSegments()[0];
        Segment baru = task.GetSegments()[indexBaru];

        Assert.Equal(splitPoint, lama.End);
        Assert.Equal(splitPoint, baru.Start);
        Assert.Equal(endAsal, baru.End);
        Assert.Equal(0, baru.BytesDone);
        Assert.False(baru.IsComplete);
    }

    [Fact]
    public void SplitFromEnd_Segmen_Baru_Index_Nya_Jumlah_Segmen_Sebelumnya()
    {
        long total = 1 * MiB;
        DownloadTask task = BuatTask("split-index", total, SegmentPlanner.Plan(total, 4));
        int jumlahSebelum = task.GetSegments().Count;

        int indexBaru = task.SplitFromEnd(0, task.GetSegments()[0].End - 4096);

        Assert.Equal(jumlahSebelum, indexBaru);
        Assert.Equal(jumlahSebelum + 1, task.GetSegments().Count);

        // Index segmen lama tidak bergeser: segmen 1..3 masih di posisinya.
        var sebelum = SegmentPlanner.Plan(total, 4);
        for (int i = 1; i < jumlahSebelum; i++)
        {
            Assert.Equal(sebelum[i].Start, task.GetSegments()[i].Start);
            Assert.Equal(sebelum[i].End, task.GetSegments()[i].End);
        }
    }

    [Fact]
    public void SplitFromEnd_NextPosition_Segmen_Lama_Tidak_Berubah()
    {
        long total = 1 * MiB;
        DownloadTask task = BuatTask("split-nextpos", total, SegmentPlanner.Plan(total, 2));

        // Segmen 0 sudah berjalan 100 KiB.
        task.Advance(0, 100 * 1024);
        Segment korban = task.GetSegments()[0];
        long nextPosSebelum = korban.NextPosition;

        long splitPoint = korban.End - 4096; // di depan area tertulis
        task.SplitFromEnd(0, splitPoint);

        Segment lama = task.GetSegments()[0];
        Assert.Equal(nextPosSebelum, lama.NextPosition);
        Assert.Equal(100 * 1024, lama.BytesDone);
        Assert.Equal(korban.Start, lama.Start);
    }

    [Fact]
    public void SplitFromEnd_Memotong_Area_Tertulis_Melempar_InvalidOperationException()
    {
        long total = 1 * MiB;
        DownloadTask task = BuatTask("split-tertulis", total, SegmentPlanner.Plan(total, 2));

        task.Advance(0, 300 * 1024);
        long nextPos = task.GetSegments()[0].NextPosition;

        // Titik potong di bawah NextPosition berarti memotong data yang sudah tertulis.
        Assert.Throws<InvalidOperationException>(() => task.SplitFromEnd(0, nextPos - 1));
        Assert.Throws<InvalidOperationException>(() => task.SplitFromEnd(0, nextPos - 4096));
    }

    [Fact]
    public void SplitFromEnd_Titik_Di_Luar_Rentang_Melempar_ArgumentOutOfRangeException()
    {
        long total = 1 * MiB;
        DownloadTask task = BuatTask("split-luar", total, SegmentPlanner.Plan(total, 2));

        Segment korban = task.GetSegments()[0];

        Assert.Throws<ArgumentOutOfRangeException>(() => task.SplitFromEnd(0, korban.Start));     // tepat Start
        Assert.Throws<ArgumentOutOfRangeException>(() => task.SplitFromEnd(0, korban.Start - 1)); // di bawah Start
        Assert.Throws<ArgumentOutOfRangeException>(() => task.SplitFromEnd(0, korban.End));       // tepat End
        Assert.Throws<ArgumentOutOfRangeException>(() => task.SplitFromEnd(0, korban.End + 1));   // di atas End
    }

    // ---------- MergeWithNext ----------

    [Fact]
    public void MergeWithNext_Dua_Segmen_Kosong_Menjadi_Satu()
    {
        var task = BuatTask("merge-ok", 300, new[]
        {
            new Segment(0, 0, 100),
            new Segment(1, 100, 300),
        });

        bool merged = task.MergeWithNext(0);

        Assert.True(merged);
        var segments = task.GetSegments();

        // Tetangga tidak dihapus dari daftar: index tetap stabil.
        Assert.Equal(2, segments.Count);

        Assert.Equal(300, segments[0].End);
        Assert.Equal(0, segments[0].BytesDone);

        // Segmen berikutnya menjadi zero-width: dianggap selesai, tak diklaim lagi.
        Assert.Equal(100, segments[1].Start);
        Assert.Equal(100, segments[1].End);
        Assert.Equal(0, segments[1].Length);
        Assert.True(segments[1].IsComplete);

        ValidasiCakupan(segments, 300);
    }

    [Fact]
    public void MergeWithNext_Segmen_Pertama_Sudah_Berjalan_Menolak()
    {
        var task = BuatTask("merge-jalan1", 300, new[]
        {
            new Segment(0, 0, 100),
            new Segment(1, 100, 300),
        });
        task.Advance(0, 50);

        Assert.False(task.MergeWithNext(0));

        // Tidak ada yang berubah.
        var segments = task.GetSegments();
        Assert.Equal(100, segments[0].End);
        Assert.Equal(300, segments[1].End);
        Assert.Equal(50, segments[0].BytesDone);
    }

    [Fact]
    public void MergeWithNext_Segmen_Berikutnya_Sudah_Berjalan_Menolak()
    {
        var task = BuatTask("merge-jalan2", 300, new[]
        {
            new Segment(0, 0, 100),
            new Segment(1, 100, 300),
        });
        task.Advance(1, 50);

        Assert.False(task.MergeWithNext(0));

        var segments = task.GetSegments();
        Assert.Equal(100, segments[0].End);
        Assert.Equal(300, segments[1].End);
        Assert.Equal(50, segments[1].BytesDone);
    }

    [Fact]
    public void MergeWithNext_Bukan_Tetangga_Menolak()
    {
        var task = BuatTask("merge-putus", 300, new[]
        {
            new Segment(0, 0, 100),
            new Segment(1, 200, 300), // ada celah [100-200)
        });

        Assert.False(task.MergeWithNext(0));

        var segments = task.GetSegments();
        Assert.Equal(100, segments[0].End);
        Assert.Equal(200, segments[1].Start);
    }

    [Fact]
    public void MergeWithNext_Di_Ujung_Daftar_Menolak()
    {
        var task = BuatTask("merge-ujung", 300, new[]
        {
            new Segment(0, 0, 300),
        });

        // Tidak ada tetangga berikutnya.
        Assert.False(task.MergeWithNext(0));
    }

    [Fact]
    public void MergeWithNext_Menggabungkan_Kembali_Hasil_Pecahan_Segmen_Terakhir()
    {
        long total = 1 * MiB;
        DownloadTask task = BuatTask("merge-balik", total, SegmentPlanner.Plan(total, 2));

        // Pecah segmen terakhir: hasil pecahan nempel di ujung daftar dan
        // bersebelahan dengan korbannya, jadi bisa digabung balik.
        Segment korban = task.GetSegments()[1];
        long endAsal = korban.End;
        int idx1 = task.SplitFromEnd(1, korban.Start + korban.Length / 2);

        Assert.True(task.MergeWithNext(1)); // gabung 1 dengan pecahan hasil split (idx1)

        var segments = task.GetSegments();
        Assert.Equal(endAsal, segments[1].End);     // survivor menyerap seluruh ekor
        Assert.Equal(0, segments[idx1].Length);     // pecahan menjadi zero-width
        Assert.True(segments[idx1].IsComplete);

        // Pecahan sudah zero-width: tidak bisa digabung sekali lagi.
        Assert.False(task.MergeWithNext(idx1));

        ValidasiCakupan(segments, total);
    }

    // ---------- Round-trip manifest ----------

    [Fact]
    public void Manifest_RoundTrip_Menyimpan_Segmen_Zero_Width_Dan_Progres()
    {
        long total = 2 * MiB;
        DownloadTask task = BuatTask("roundtrip", total, SegmentPlanner.Plan(total, 4));

        // Tiru kondisi runtime: satu segmen berjalan, satu dipecah, satu digabung.
        task.Advance(0, 128 * 1024);
        Segment seg1 = task.GetSegments()[1];
        int idxBaru = task.SplitFromEnd(1, seg1.Start + seg1.Length / 2);
        task.MergeWithNext(2);

        task.SetState(DownloadState.Paused);

        DownloadManifest manifest = task.ToManifest();
        DownloadTask pulih = DownloadTask.FromManifest(manifest);

        var asal = task.GetSegments();
        var hasil = pulih.GetSegments();

        Assert.Equal(asal.Count, hasil.Count);
        for (int i = 0; i < asal.Count; i++)
        {
            Assert.Equal(asal[i].Index, hasil[i].Index);
            Assert.Equal(asal[i].Start, hasil[i].Start);
            Assert.Equal(asal[i].End, hasil[i].End);
            Assert.Equal(asal[i].BytesDone, hasil[i].BytesDone);
        }

        // Segmen zero-width ikut tersimpan dan tetap zero-width setelah pulih.
        Assert.Contains(hasil, s => s.Length == 0 && s.IsComplete);

        ValidasiCakupan(hasil, total);
        Assert.Equal(DownloadState.Paused, pulih.State);
    }

    // ---------- Stres ringan (multithread) ----------

    /// <summary>
    /// Beberapa task diproses paralel oleh tiga thread yang bergantian memanggil
    /// Advance, SplitFromEnd, dan MergeWithNext ribuan kali. Operasi boleh kalah
    /// balapan (melempar pengecualian yang wajar), tetapi invarian cakupan dan
    /// progres harus tetap terjaga di akhir.
    /// </summary>
    [Fact]
    public async Task Operasi_Segmen_Paralel_Menjaga_Invarian()
    {
        const int jumlahTask = 8;
        const int iterasiPerThread = 2000;

        long total = 1 * MiB;
        var tasks = Enumerable.Range(0, jumlahTask)
            .Select(i => BuatTask($"stres-{i}", total, SegmentPlanner.Plan(total, 4)))
            .ToList();

        void KerjaStres(DownloadTask task, int seed)
        {
            var random = new Random(seed);

            void SatuOperasi()
            {
                int op = random.Next(3);
                var segments = task.GetSegments();
                int index = random.Next(segments.Count);

                switch (op)
                {
                    case 0:
                        // Advance selalu aman: delta di-clamp ke Length.
                        task.Advance(index, random.Next(1, 512));
                        break;

                    case 1:
                        {
                            // Pecah ujung ekor segmen; balapan dengan thread lain
                            // bisa melempar pengecualian yang wajar — diabaikan.
                            Segment s = task.GetSegments()[index];
                            long sisa = s.Remaining;
                            if (sisa <= 1) return;
                            long splitPoint = s.End - random.Next(1, (int)Math.Min(int.MaxValue, sisa));
                            try { task.SplitFromEnd(index, splitPoint); }
                            catch (InvalidOperationException) { }
                            catch (ArgumentOutOfRangeException) { }
                            break;
                        }

                    default:
                        // Merge tidak pernah melempar; gagal balapan = false.
                        task.MergeWithNext(index);
                        break;
                }
            }

            // Tiga thread bersaing di atas satu task.
            var threads = new Thread[3];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < iterasiPerThread; i++) SatuOperasi();
                });
            }

            foreach (Thread t in threads) t.Start();
            foreach (Thread t in threads) t.Join();
        }

        await Task.WhenAll(tasks.Select((task, i) => Task.Run(() => KerjaStres(task, 12345 + i))));

        foreach (DownloadTask task in tasks)
        {
            ValidasiCakupan(task.GetSegments(), total);
        }
    }

    [Fact]
    public void Segment_ZeroWidth_Sah_Dan_Dianggap_Selesai()
    {
        // Konstruktor sekarang menerima End == Start (segmen zero-width hasil merge).
        var segment = new Segment(0, 100, 100);

        Assert.Equal(0, segment.Length);
        Assert.True(segment.IsComplete);
        Assert.Null(segment.BuildRangeHeader());
        Assert.Equal(100, segment.NextPosition);
    }

    [Fact]
    public void Segment_TruncateDanExtend_Hanya_Satu_Arah()
    {
        var segment = new Segment(0, 0, 1000);

        segment.TruncateTo(400);
        Assert.Equal(400, segment.End);

        // Boleh sampai zero-width.
        segment.TruncateTo(segment.Start);
        Assert.Equal(0, segment.Length);
        Assert.True(segment.IsComplete);

        segment.ExtendTo(700);
        Assert.Equal(700, segment.End);

        Assert.Throws<ArgumentOutOfRangeException>(() => segment.TruncateTo(800)); // mundur dari maju
        Assert.Throws<ArgumentOutOfRangeException>(() => segment.ExtendTo(600));   // maju dari mundur
    }
}
