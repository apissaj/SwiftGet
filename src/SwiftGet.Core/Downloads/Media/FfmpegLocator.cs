using System.Diagnostics;

namespace SwiftGet.Core.Downloads.Media;

/// <summary>
/// Mencari ffmpeg untuk penggabungan jalur video dan audio DASH.
/// Urutan: lokasi yang diatur pengguna, folder aplikasi, lalu PATH.
/// ffmpeg tidak dibundel supaya aplikasi tetap ringkas; kegiatannya
/// hanya penggabungan stream yang sudah lengkap (tanpa encode ulang).
/// </summary>
public static class FfmpegLocator
{
    public static string? Find(string? configuredPath) =>
        Find(configuredPath, AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("PATH"));

    internal static string? Find(string? configuredPath, string? baseDirectory, string? pathEnv)
    {
        // Lokasi yang diatur pengguna bersifat mutlak: salah lokasi berarti
        // gagal dengan pesan, bukan diam-diam memakai ffmpeg versi lain.
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        if (!string.IsNullOrEmpty(baseDirectory) &&
            File.Exists(Path.Combine(baseDirectory, "ffmpeg.exe")))
            return Path.Combine(baseDirectory, "ffmpeg.exe");

        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Entri PATH yang tidak sah dilewati.
            }
        }

        return null;
    }

    /// <summary>
    /// Gabungkan jalur video dan audio menjadi satu berkas MP4 tanpa encode
    /// ulang (-c copy). Mengembalikan pesan galat dari ffmpeg bila gagal.
    /// </summary>
    public static async Task MuxAsync(
        string ffmpegPath, string videoPath, string audioPath, string outputPath,
        CancellationToken ct = default)
    {
        // ffmpeg memperlakukan path output sebagai URL: backslash Windows
        // membuat hasil ditulis ke folder kerja tanpa galat. Semua path
        // dipaksa forward slash yang diterima ffmpeg di Windows.
        string video = videoPath.Replace('\\', '/');
        string audio = audioPath.Replace('\\', '/');
        string output = outputPath.Replace('\\', '/');

        var start = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -i \"{video}\" -i \"{audio}\" " +
                        "-c copy -movflags +faststart " +
                        $"\"{output}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using Process process = Process.Start(start)
            ?? throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "ffmpeg tidak bisa dijalankan.");

        string stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            // Baris terakhir stderr biasanya berisi penjelasan yang berguna.
            string tail = string.Join("\n", stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^3..]);
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                $"ffmpeg gagal menggabungkan jalur (kode {process.ExitCode}):\n{tail}");
        }

        if (!File.Exists(outputPath))
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "ffmpeg melaporkan sukses tetapi berkas hasil tidak ditemukan.");
    }
}
