using SwiftGet.Core.Downloads;
using Xunit;

namespace SwiftGet.Tests;

public sealed class SegmentPlannerTests
{
    [Theory]
    [InlineData(1024L * 1024, 1)]
    [InlineData(10L * 1024 * 1024, 4)]
    [InlineData(10L * 1024 * 1024, 8)]
    [InlineData(100L * 1024 * 1024, 16)]
    [InlineData(123456789L, 8)]
    public void Plan_Menutupi_Seluruh_File_Tanpa_Celah(long total, int connections)
    {
        var segments = SegmentPlanner.Plan(total, connections);

        Assert.True(SegmentPlanner.IsValidPlan(segments, total),
            $"Rencana tidak valid: {string.Join(", ", segments)}");

        Assert.Equal(0, segments[0].Start);
        Assert.Equal(total, segments[^1].End);
    }

    [Fact]
    public void Plan_Menghormati_Batas_Koneksi()
    {
        long total = 50L * 1024 * 1024;
        var segments = SegmentPlanner.Plan(total, desiredConnections: 100, maxConnections: 4);

        Assert.True(SegmentPlanner.IsValidPlan(segments, total));
        Assert.True(segments.Count <= 4, $"Diharapkan <=4 segmen, dapat {segments.Count}");
    }

    [Fact]
    public void Plan_Tidak_Membuat_Segmen_Terlalu_Kecil()
    {
        // 1 MiB dengan 32 koneksi: harus jauh lebih sedikit dari 32 segmen.
        long total = 1024L * 1024;
        var segments = SegmentPlanner.Plan(total, 32);

        Assert.True(SegmentPlanner.IsValidPlan(segments, total));
        Assert.True(segments.Count <= 4,
            $"Segmen terlalu banyak untuk file 1 MiB: {segments.Count}");
    }

    [Fact]
    public void Plan_File_Sangat_Kecil_Menghasilkan_Satu_Segmen()
    {
        long total = 100;
        var segments = SegmentPlanner.Plan(total, 8);

        var single = Assert.Single(segments);
        Assert.Equal(0, single.Start);
        Assert.Equal(total, single.End);
    }

    [Fact]
    public void Plan_Satu_Koneksi_Menutupi_Seluruh_File()
    {
        long total = 5L * 1024 * 1024;
        var segments = SegmentPlanner.Plan(total, 1);

        var single = Assert.Single(segments);
        Assert.Equal(0, single.Start);
        Assert.Equal(total, single.End);
    }

    [Fact]
    public void Segment_BuildRangeHeader_Menggunakan_Range_Inklusif()
    {
        var segment = new Segment(0, 1000, 2000);

        Assert.Equal("bytes=1000-1999", segment.BuildRangeHeader());
    }

    [Fact]
    public void Segment_BuildRangeHeader_Menyesuaikan_Progres()
    {
        var segment = new Segment(0, 1000, 2000);
        segment.BytesDone = 500;

        Assert.Equal("bytes=1500-1999", segment.BuildRangeHeader());
        Assert.Equal(500, segment.Remaining);
    }

    [Fact]
    public void Segment_Selesai_Mengembalikan_Null()
    {
        var segment = new Segment(0, 0, 100) { BytesDone = 100 };

        Assert.True(segment.IsComplete);
        Assert.Null(segment.BuildRangeHeader());
    }
}
