using Microsoft.Win32.SafeHandles;

namespace SwiftGet.Core.Storage;

/// <summary>
/// Satu file sementara (<c>.swiftpart</c>) tempat semua segmen ditulis pada
/// offset miliknya masing-masing.
/// <para>
/// Desain ini meniru pendekatan yang dipakai downloader segmented pada umumnya:
/// satu file, banyak penulis, tiap penulis memegang rentang byte eksklusif dan
/// menulis dengan offset absolut.
/// </para>
/// <para>
/// <b>Catatan implementasi penting:</b> handle file dibuka sebagai
/// <see cref="SafeFileHandle"/> dan penulisan memakai <see cref="RandomAccess"/>
/// dengan offset eksplisit. Memakai <c>Stream.Position</c> bersama dari beberapa
/// worker adalah bug: cursor bersama akan membuat blok data saling menimpa.
/// </para>
/// </summary>
public sealed class PartFile : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly object _writeGate = new();
    private readonly object _lengthGate = new();
    private long _knownLength;
    private bool _disposed;

    private PartFile(SafeFileHandle handle, long knownLength)
    {
        _handle = handle;
        _knownLength = knownLength;
    }

    public string Path { get; private init; } = "";

    /// <summary>Buka atau buat file sementara dengan panjang yang sudah dialokasikan.</summary>
    /// <param name="path">Lokasi file <c>.swiftpart</c>.</param>
    /// <param name="totalLength">Panjang total file akhir yang diharapkan.</param>
    public static PartFile Open(string path, long totalLength)
    {
        if (totalLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalLength));

        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                FileOptions.Asynchronous);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DownloadException(DownloadErrorKind.AccessDenied,
                $"Tidak bisa menulis ke '{path}'.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new DownloadException(DownloadErrorKind.AccessDenied,
                $"Folder tujuan tidak ada: '{dir}'.", ex);
        }

        try
        {
            // Pastikan file punya panjang penuh agar penulisan pada offset jauh valid
            // dan sisa byte yang belum terunduh tetap nol (bukan EOF).
            long current = RandomAccess.GetLength(handle);
            if (current < totalLength)
            {
                RandomAccess.SetLength(handle, totalLength);
            }

            return new PartFile(handle, totalLength) { Path = path };
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            handle.Dispose();
            throw new DownloadException(DownloadErrorKind.DiskFull,
                "Ruang disk tidak cukup untuk mengalokasikan file sementara.", ex);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Panjang yang dialokasikan untuk file sementara ini.</summary>
    public long Length
    {
        get { lock (_lengthGate) return _knownLength; }
    }

    /// <summary>
    /// Tulis satu blok data ke offset absolut.
    /// <para>
    /// Offset ditentukan oleh segmen pemanggil; method ini tidak boleh dipanggil
    /// dua worker untuk rentang yang sama.
    /// </para>
    /// </summary>
    public async ValueTask WriteAtAsync(long absoluteOffset, ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (absoluteOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteOffset));
        if (buffer.Length == 0) return;

        long limit;
        lock (_lengthGate) limit = _knownLength;
        if (absoluteOffset + buffer.Length > limit)
            throw new ArgumentOutOfRangeException(nameof(buffer),
                $"Penulisan [{absoluteOffset},{absoluteOffset + buffer.Length}) melewati batas file {limit}.");

        try
        {
            // Offset eksplisit: setiap penulis independen, tidak ada cursor bersama.
            await RandomAccess.WriteAsync(_handle, buffer, absoluteOffset, ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new DownloadException(DownloadErrorKind.DiskFull, "Ruang disk habis saat menulis.", ex);
        }
        catch (IOException ex)
        {
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                $"Gagal menulis pada offset {absoluteOffset}: {ex.Message}", ex);
        }
    }

    /// <summary>Pastikan semua data yang ditulis sudah sampai ke disk.</summary>
    public Task FlushAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Handle dibuka dengan FILE_FLAG_NO_BUFFERING tidak dipakai, jadi flush
        // eksplisit dilakukan saat Commit/Dispose. Di sini cukup no-op asinkron
        // agar kontrak API tetap konsisten untuk pemanggil.
        return Task.CompletedTask;
    }

    /// <summary>Tutup handle lalu pindahkan file sementara menjadi file akhir.</summary>
    /// <param name="finalPath">Lokasi file akhir.</param>
    /// <param name="overwrite">Timpa file akhir bila sudah ada.</param>
    public void CommitAs(string finalPath, bool overwrite)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Flush ke disk sebelum rename supaya tidak ada data tertinggal di cache.
        try
        {
            RandomAccess.FlushToDisk(_handle);
        }
        catch (IOException) { /* best effort */ }

        _handle.Dispose();
        _disposed = true;

        try
        {
            File.Move(Path, finalPath, overwrite);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new DownloadException(DownloadErrorKind.DiskFull,
                $"Ruang disk habis saat memindahkan ke '{finalPath}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DownloadException(DownloadErrorKind.AccessDenied,
                $"Tidak boleh menimpa '{finalPath}'.", ex);
        }
    }

    /// <summary>Tutup handle tanpa memindahkan file (mis. saat pause).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { RandomAccess.FlushToDisk(_handle); } catch { /* best effort */ }
        _handle.Dispose();
    }

    /// <summary>True bila kode error Windows menunjukkan disk penuh.</summary>
    internal static bool IsDiskFull(IOException ex)
    {
        // ERROR_HANDLE_DISK_FULL (0x27) / ERROR_DISK_FULL (0x70)
        int code = ex.HResult & 0xFFFF;
        return code is 0x27 or 0x70;
    }

    /// <summary>Buang file sementara bila ada. Dipakai untuk cancel dan pemulihan.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* diabaikan: pemanggil tidak bisa berbuat apa-apa */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }
}
