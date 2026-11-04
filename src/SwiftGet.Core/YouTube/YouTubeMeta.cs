using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SwiftGet.Core.YouTube;

/// <summary>Satu pilihan kualitas dari yt-dlp (video-saja atau progresif).</summary>
public sealed class YouTubeFormat
{
    public required string Url { get; init; }
    public required string FormatId { get; init; }
    public required string Ext { get; init; }
    public string? Vcodec { get; init; }
    public string? Acodec { get; init; }
    public int? Height { get; init; }
    public long? FilesizeBytes { get; init; }
}

/// <summary>Hasil probe yt-dlp: judul + daftar format video + audio terbaik.</summary>
public sealed class YouTubeMeta
{
    public required string Title { get; init; }
    public string? Error { get; init; } = null;
    public List<YouTubeFormat> Video { get; init; } = new();

    /// <summary>Audio terbaik (m4a lebih disukai agar mux ke MP4 tanpa re-encode); null bila tak ada.</summary>
    public YouTubeFormat? AudioTerbaik { get; set; }
}

/// <summary>
/// Jembatan YouTube via yt-dlp: URL watch YouTube tidak bisa diunduh langsung
/// (SABR — terbukti di riset WinDivert), tetapi yt-dlp membuka daftar format
/// dengan URL langsung yang sah. SwiftGet lalu mengunduh video + audio dengan
/// mesinnya sendiri dan menggabungkannya lewat ffmpeg.
/// </summary>
public static class YouTube
{
    public static bool IsYouTubeUrl(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        return host == "youtube.com" || host == "youtu.be" ||
               host == "www.youtube.com" || host == "m.youtube.com" ||
               host == "music.youtube.com" || host == "www.youtube-nocookie.com" ||
               host.EndsWith(".youtube.com");
    }

    /// <summary>Jalankan yt-dlp -J dan uraikan hasilnya.</summary>
    public static async Task<YouTubeMeta> AmbilAsync(string ytDlpExe, string url, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpExe,
            Arguments = $"-J --no-warnings \"{url}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("yt-dlp tidak bisa dijalankan.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            string alasan = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? $"kode keluar {process.ExitCode}";
            var gagal = new YouTubeMeta { Title = "", Error = $"yt-dlp gagal: {alasan}" };
            return gagal;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            string? galat = null;

            if (root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
                galat = "Siaran langsung tidak didukung.";

            if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
                galat = "yt-dlp tidak melaporkan format apa pun.";

            if (galat is not null)
                return new YouTubeMeta { Title = root.TryGetProperty("title", out var judulAwal) ? judulAwal.GetString() ?? "" : "", Error = galat };

            var meta = new YouTubeMeta
            {
                Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            };

            YouTubeFormat? audioTerbaik = null;
            long audioSkor = -1;

            foreach (var f in formats.EnumerateArray())
            {
                string? urlFmt = f.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(urlFmt)) continue;

                string protocol = f.TryGetProperty("protocol", out var pr) ? pr.GetString() ?? "" : "";
                if (protocol.StartsWith("m3u8", StringComparison.Ordinal)) continue; // playlist HLS, bukan berkas

                string vcodec = f.TryGetProperty("vcodec", out var vc) ? vc.GetString() ?? "none" : "none";
                string acodec = f.TryGetProperty("acodec", out var ac) ? ac.GetString() ?? "none" : "none";
                string ext = f.TryGetProperty("ext", out var ex) ? ex.GetString() ?? "mp4" : "mp4";
                string formatId = f.TryGetProperty("format_id", out var fid) ? fid.GetString() ?? "" : "";
                int? height = f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number
                    ? h.GetInt32() : null;
                long? ukuran = null;
                if (f.TryGetProperty("filesize", out var fsz) && fsz.ValueKind == JsonValueKind.Number)
                    ukuran = fsz.GetInt64();
                else if (f.TryGetProperty("filesize_approx", out var fsa) && fsa.ValueKind == JsonValueKind.Number)
                    ukuran = fsa.GetInt64();

                bool adaVideo = vcodec != "none";
                bool adaAudio = acodec != "none";

                if (adaVideo)
                {
                    meta.Video.Add(new YouTubeFormat
                    {
                        Url = urlFmt, FormatId = formatId, Ext = ext,
                        Vcodec = vcodec, Acodec = acodec, Height = height, FilesizeBytes = ukuran,
                    });
                }
                else if (adaAudio)
                {
                    // m4a lebih disukai: mux ke MP4 tanpa re-encode.
                    long skor = (ukuran ?? 0) + (ext == "m4a" ? 1 : 0);
                    if (skor > audioSkor)
                    {
                        audioSkor = skor;
                        audioTerbaik = new YouTubeFormat
                        {
                            Url = urlFmt, FormatId = formatId, Ext = ext,
                            Vcodec = vcodec, Acodec = acodec, Height = null, FilesizeBytes = ukuran,
                        };
                    }
                }
            }

            meta.Video.Sort((a, b) => (b.Height ?? 0) - (a.Height ?? 0));
            meta.AudioTerbaik = audioTerbaik;
            return meta;
        }
        catch (JsonException ex)
        {
            return new YouTubeMeta { Title = "", Error = "Keluaran yt-dlp tidak bisa diurai: " + ex.Message };
        }
    }

    /// <summary>Bersihkan judul menjadi nama berkas yang sah dan ringkas.</summary>
    public static string BersihkanNama(string judul)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var nama = new string(judul.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (nama.Length > 150) nama = nama[..150];
        return string.IsNullOrWhiteSpace(nama) ? "video-youtube" : nama;
    }
}
