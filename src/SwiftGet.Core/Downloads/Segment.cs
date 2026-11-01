namespace SwiftGet.Core.Downloads;

/// <summary>
/// Satu rentang byte yang diunduh oleh satu worker.
/// <para>
/// <see cref="Start"/> inklusif dan <see cref="End"/> eksklusif, sehingga
/// panjang segmen = <c>End - Start</c>. Konvensi ini dipakai konsisten di seluruh
/// engine supaya penulisan file tidak pernah meleset satu byte.
/// </para>
/// </summary>
public sealed class Segment
{
    public Segment(int index, long start, long end)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end), "End tidak boleh di bawah Start.");

        Index = index;
        Start = start;
        End = end;
    }

    /// <summary>Urutan segmen dalam file; dipakai untuk penamaan file sementara dan log.</summary>
    public int Index { get; }

    /// <summary>Offset byte awal (inklusif) di file akhir.</summary>
    public long Start { get; }

    /// <summary>
    /// Offset byte akhir (eksklusif) di file akhir. Bisa berubah saat runtime
    /// oleh dynamic segmentation: pemecahan memotong End, penggabungan
    /// memperluasnya. Start dan BytesDone tidak pernah disentuh operasi itu,
    /// jadi posisi tulis worker tidak pernah basi.
    /// </summary>
    public long End { get; internal set; }

    /// <summary>Panjang segmen dalam byte.</summary>
    public long Length => End - Start;

    /// <summary>Jumlah byte yang sudah tertulis ke disk untuk segmen ini.</summary>
    public long BytesDone { get; internal set; }

    /// <summary>True bila seluruh rentang segmen sudah diterima.</summary>
    public bool IsComplete => BytesDone >= Length;

    /// <summary>Offset absolut berikutnya yang harus diminta ke server.</summary>
    public long NextPosition => Start + BytesDone;

    /// <summary>Sisa byte yang belum diterima.</summary>
    public long Remaining => Math.Max(0, Length - BytesDone);

    /// <summary>Potong ujung akhir segmen (pemecahan). Hanya boleh mundur.</summary>
    internal void TruncateTo(long end)
    {
        if (end < Start) throw new ArgumentOutOfRangeException(nameof(end), "Hasil potong tidak boleh zero-width negatif.");
        if (end > End) throw new ArgumentOutOfRangeException(nameof(end), "TruncateTo tidak boleh memperbesar End.");
        End = end;
    }

    /// <summary>Perluas ujung akhir segmen (penggabungan). Hanya boleh maju.</summary>
    internal void ExtendTo(long end)
    {
        if (end < End) throw new ArgumentOutOfRangeException(nameof(end), "ExtendTo tidak boleh memperkecil End.");
        End = end;
    }

    /// <summary>
    /// Header <c>Range</c> untuk sisa segmen ini. Mengembalikan <c>null</c> bila
    /// segmen sudah selesai.
    /// </summary>
    public string? BuildRangeHeader()
    {
        if (IsComplete) return null;
        long from = NextPosition;
        long to = End - 1; // Range inklusif di sisi server
        return $"bytes={from}-{to}";
    }

    public override string ToString() =>
        $"#{Index} [{Start}-{End}) {BytesDone}/{Length}";
}
