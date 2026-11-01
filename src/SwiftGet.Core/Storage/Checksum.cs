using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SwiftGet.Core.Storage;

/// <summary>
/// Pemerinta integritas berkas hasil unduhan.
/// <para>
/// Dipakai sebagai verifikasi opsional setelah unduhan selesai: bila server
/// menyediakan checksum (<c>Digest</c>, <c>Content-MD5</c>, <c>X-Checksum-*</c>,
/// atau <c>X-Goog-Hash</c>), hasil unduhan bisa dipastikan tidak rusak akibat
/// koneksi paralel.
/// </para>
/// </summary>
public static class Checksum
{
    /// <summary>
    /// Hitung hash berkas dengan algoritma bernama ("sha256", "sha384",
    /// "sha512", "sha1", atau "md5", huruf besar-kecil diabaikan). Dibaca
    /// sebagai stream agar file besar tidak perlu dimuat ke memori.
    /// Mengembalikan null bila algoritma tidak dikenal — pemanggil memutuskan
    /// sendiri artinya: lewati verifikasi, bukan nyatakan berkas korup.
    /// </summary>
    public static async Task<string?> ComputeAsync(string path, string algorithm, CancellationToken ct = default)
    {
        Func<Stream, CancellationToken, ValueTask<byte[]>>? hash = algorithm.ToLowerInvariant() switch
        {
            "sha256" => SHA256.HashDataAsync,
            "sha384" => SHA384.HashDataAsync,
            "sha512" => SHA512.HashDataAsync,
            "sha1" => SHA1.HashDataAsync,
            "md5" => MD5.HashDataAsync,
            _ => null,
        };

        if (hash is null) return null;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] digest = await hash(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Parse header <c>Digest</c> (RFC 3230, bisa berisi beberapa pasangan
    /// dipisah koma) dan kembalikan pasangan algoritma/nilai yang dikenali.
    /// Nama algoritma dinormalisasi: <c>sha-256</c> menjadi <c>sha256</c>,
    /// <c>sha</c> dan <c>sha-1</c> menjadi <c>sha1</c>, dst.
    /// </summary>
    public static IReadOnlyList<(string Algorithm, string ValueBase64)> ParseDigestHeader(string? value)
    {
        var results = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(value)) return results;

        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;

            string rawAlgorithm = part[..eq].Trim().ToLowerInvariant();
            string body = part[(eq + 1)..].Trim().Trim('"');

            // "sha-256" -> "sha256"; "sha" dan "sha-1" keduanya SHA-1 (RFC 3230).
            string algorithm = rawAlgorithm.Replace("-", "");
            if (algorithm == "sha") algorithm = "sha1";

            if (algorithm is "sha256" or "sha384" or "sha512" or "sha1" or "md5")
                results.Add((algorithm, body));
        }

        return results;
    }
}
