using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SwiftGet.Core.Storage;

/// <summary>
/// Pemerinta integritas berkas hasil unduhan.
/// <para>
/// Dipakai sebagai verifikasi opsional setelah unduhan selesai: bila server
/// menyediakan checksum (<c>Digest</c>, <c>Content-MD5</c>, atau <c>X-Checksum-*</c>),
/// hasil unduhan bisa dipastikan tidak rusak akibat koneksi paralel.
/// </para>
/// </summary>
public static class Checksum
{
    /// <summary>
    /// Hitung hash SHA-256 dari berkas. Dibaca sebagai stream agar file besar
    /// tidak perlu dimuat seluruhnya ke memori.
    /// </summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] hash = await System.Security.Cryptography.SHA256
            .HashDataAsync(stream, ct)
            .ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Hitung MD5 (untuk server yang masih memakai <c>Content-MD5</c>).</summary>
    public static async Task<string> Md5Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] hash = await System.Security.Cryptography.MD5
            .HashDataAsync(stream, ct)
            .ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Parse header <c>Digest: sha-256=base64</c> (RFC 3230) dan kembalikan
    /// pasangan algoritma/nilai yang dikenali.
    /// </summary>
    public static IReadOnlyList<(string Algorithm, string ValueBase64)> ParseDigestHeader(string? value)
    {
        var results = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(value)) return results;

        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;

            string algorithm = part[..eq].Trim().ToLowerInvariant();
            string body = part[(eq + 1)..].Trim().Trim('"');

            if (algorithm.Contains("sha-256")) results.Add(("sha256", body));
            else if (algorithm.Contains("md5")) results.Add(("md5", body));
        }

        return results;
    }
}
