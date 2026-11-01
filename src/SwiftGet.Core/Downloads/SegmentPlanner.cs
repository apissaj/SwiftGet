namespace SwiftGet.Core.Downloads;

/// <summary>
/// Membagi ukuran file menjadi beberapa segmen yang akan diunduh paralel.
/// <para>
/// MVP memakai pembagian statis di awal (sesuai keputusan proyek). Rumusnya
/// sama dengan yang dipakai banyak downloader: bagi rata, lalu ratakan batas
/// segmen ke kelipatan <see cref="AlignmentBytes"/> supaya penulisan disk
/// berjalan pada batas yang rapi.
/// </para>
/// </summary>
public static class SegmentPlanner
{
    /// <summary>Ukuran minimum satu segmen. Di bawah ini, paralelisasi tidak sepadan.</summary>
    public const long MinSegmentSize = 256 * 1024; // 256 KiB

    /// <summary>Batas pembulatan posisi segmen agar tidak terlalu banyak segmen mikro.</summary>
    public const long AlignmentBytes = 4096;

    /// <summary>
    /// Rencanakan segmen untuk <paramref name="totalLength"/> byte.
    /// </summary>
    /// <param name="totalLength">Panjang total file.</param>
    /// <param name="desiredConnections">Jumlah koneksi yang diinginkan.</param>
    /// <param name="maxConnections">Batas atas koneksi (dari konfigurasi).</param>
    /// <returns>Daftar segmen yang menutupi [0, totalLength) tanpa celah dan tanpa tumpang tindih.</returns>
    public static List<Segment> Plan(long totalLength, int desiredConnections, int maxConnections = 32)
    {
        if (totalLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalLength), "Panjang harus positif.");

        int connections = Math.Clamp(desiredConnections, 1, Math.Max(1, maxConnections));

        // Jangan membuat segmen lebih kecil dari MinSegmentSize.
        long byMin = Math.Max(1, totalLength / MinSegmentSize);
        connections = (int)Math.Min(connections, byMin);
        connections = Math.Max(1, connections);

        if (connections == 1)
        {
            // Satu segmen harus menutupi seluruh file, termasuk kasus file sangat kecil.
            return new List<Segment> { new(0, 0, totalLength) };
        }

        var segments = new List<Segment>(connections);
        long chunk = totalLength / connections;

        long cursor = 0;
        for (int i = 0; i < connections; i++)
        {
            bool last = i == connections - 1;

            long end;
            if (last)
            {
                // Segmen terakhir menyerap sisa pembagian. Ini menjamin cakupan penuh.
                end = totalLength;
            }
            else
            {
                long tentative = cursor + chunk;
                end = AlignDown(tentative);

                // Jaga agar tidak menghasilkan segmen kosong/terlalu kecil.
                if (end - cursor < MinSegmentSize)
                    end = tentative;
            }

            if (end <= cursor)
            {
                // Pembulatan terlalu agresif; pakai pembagian mentah sebagai jaring aman.
                end = last ? totalLength : cursor + chunk;
            }

            segments.Add(new Segment(i, cursor, end));
            cursor = end;

            if (cursor >= totalLength) break;
        }

        // Kalau karena pembulatan ada sisa yang tidak tercakup, perluas segmen terakhir.
        var lastSegment = segments[^1];
        if (lastSegment.End < totalLength)
        {
            segments[^1] = new Segment(lastSegment.Index, lastSegment.Start, totalLength);
        }

        return segments;
    }

    private static long AlignDown(long value)
    {
        if (value <= 0) return 0;
        return value - (value % AlignmentBytes);
    }

    /// <summary>
    /// Validasi invarian yang wajib dipenuhi rencana segmen: menutupi seluruh
    /// file, terurut, tanpa celah, tanpa tumpang tindih.
    /// </summary>
    public static bool IsValidPlan(IReadOnlyList<Segment> segments, long totalLength)
    {
        if (segments.Count == 0) return false;
        if (segments[0].Start != 0) return false;

        long expected = 0;
        foreach (var s in segments)
        {
            if (s.Start != expected) return false;
            if (s.End <= s.Start) return false;
            expected = s.End;
        }

        return expected == totalLength;
    }
}
