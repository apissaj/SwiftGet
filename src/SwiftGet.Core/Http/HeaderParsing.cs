using System.Text;

namespace SwiftGet.Core.Http;

/// <summary>
/// Parser/pembuat header HTTP yang dipakai engine. Dipisahkan dari jaringan supaya
/// bisa diuji tanpa server.
/// </summary>
public static class HeaderParsing
{
    /// <summary>
    /// Parse nilai <c>Content-Range</c> berbentuk <c>bytes 0-499/1234</c>.
    /// Mengembalikan <c>null</c> bila format tidak dikenali atau total unknown (<c>*</c>).
    /// </summary>
    public static RangeSpan? ParseContentRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Format: "bytes <start>-<end>/<total>" — unit case-insensitive.
        string text = value.Trim();
        const string prefix = "bytes ";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        text = text[prefix.Length..].Trim();

        int slash = text.IndexOf('/');
        if (slash <= 0) return null;

        string rangePart = text[..slash];
        string totalPart = text[(slash + 1)..];

        int dash = rangePart.IndexOf('-');
        if (dash <= 0) return null;

        if (!long.TryParse(rangePart[..dash], out long start)) return null;
        if (!long.TryParse(rangePart[(dash + 1)..], out long end)) return null;

        long? total = null;
        if (totalPart != "*" && long.TryParse(totalPart, out long parsedTotal))
            total = parsedTotal;

        if (end < start) return null;

        return new RangeSpan(start, end + 1, total);
    }

    /// <summary>
    /// Ekstrak nama file dari header <c>Content-Disposition</c>.
    /// Mendukung <c>filename*=UTF-8''...</c> (RFC 5987) dan <c>filename="..."</c>.
    /// </summary>
    public static string? ParseContentDispositionFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Prioritas RFC 5987: filename* lebih benar daripada filename.
        string? extended = ExtractParameter(value, "filename*");
        if (extended is not null)
        {
            string decoded = DecodeRfc5987(extended);
            if (!string.IsNullOrWhiteSpace(decoded)) return Sanitize(decoded);
        }

        string? plain = ExtractParameter(value, "filename");
        if (plain is not null && !string.IsNullOrWhiteSpace(plain))
            return Sanitize(plain);

        return null;
    }

    /// <summary>
    /// True bila nilai <c>Accept-Ranges</c> menyatakan dukungan byte range.
    /// <para>
    /// <c>none</c> harus ditolak lebih dulu: memeriksa kata "bytes" saja tidak
    /// cukup karena nilai lain bisa saja memuatnya.
    /// </para>
    /// </summary>
    public static bool AcceptsByteRanges(string? acceptRanges)
    {
        if (acceptRanges is null) return false;

        string value = acceptRanges.Trim();
        if (value.Length == 0) return false;

        // Server secara eksplisit menyatakan tidak mendukung Range.
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;

        // Bisa berisi daftar: "bytes", "bytes, bytes", atau unit lain.
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Trim().Equals("bytes", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Bentuk header <c>Range</c> inklusif dari posisi awal sampai akhir (0-based).</summary>
    public static string BuildRangeHeader(long fromInclusive, long toInclusive) =>
        $"bytes={fromInclusive}-{toInclusive}";

    private static string? ExtractParameter(string header, string name)
    {
        // Cari "name=" dengan batas kata, tanpa peduli huruf besar/kecil.
        int searchFrom = 0;
        while (searchFrom < header.Length)
        {
            int idx = header.IndexOf(name, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int after = idx + name.Length;
            // Pastikan ini parameter utuh: sebelumnya ';' atau whitespace, sesudahnya '='.
            bool boundaryBefore = idx == 0 || header[idx - 1] is ';' or ',' or ' ' or '\t';
            if (boundaryBefore && after < header.Length && header[after] == '=')
            {
                string rest = header[(after + 1)..].TrimStart();

                if (rest.StartsWith('"'))
                {
                    int closing = rest.IndexOf('"', 1);
                    return closing > 0 ? rest[1..closing] : rest[1..];
                }

                int semicolon = rest.IndexOf(';');
                return (semicolon >= 0 ? rest[..semicolon] : rest).Trim();
            }

            searchFrom = after;
        }

        return null;
    }

    private static string DecodeRfc5987(string value)
    {
        // Format: charset'lang'percent-encoded
        int first = value.IndexOf('\'');
        if (first < 0) return Uri.UnescapeDataString(value);
        int second = value.IndexOf('\'', first + 1);
        if (second < 0) return Uri.UnescapeDataString(value);

        string charset = value[..first];
        string encoded = value[(second + 1)..];

        try
        {
            byte[] bytes = UnescapeToBytes(encoded);
            Encoding encoding = charset.ToLowerInvariant() switch
            {
                "utf-8" or "utf8" => Encoding.UTF8,
                "iso-8859-1" or "latin1" => Encoding.Latin1,
                _ => Encoding.UTF8,
            };
            return encoding.GetString(bytes);
        }
        catch (FormatException)
        {
            return encoded;
        }
    }

    private static byte[] UnescapeToBytes(string encoded)
    {
        var bytes = new List<byte>(encoded.Length);
        int i = 0;
        while (i < encoded.Length)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length &&
                Uri.IsHexDigit(encoded[i + 1]) && Uri.IsHexDigit(encoded[i + 2]))
            {
                bytes.Add((byte)((Uri.FromHex(encoded[i + 1]) << 4) | Uri.FromHex(encoded[i + 2])));
                i += 3;
            }
            else
            {
                bytes.Add((byte)encoded[i]);
                i++;
            }
        }
        return bytes.ToArray();
    }

    /// <summary>Buang karakter yang ilegal pada nama file Windows dan batasi panjangnya.</summary>
    public static string Sanitize(string fileName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(fileName.Length);
        foreach (char c in fileName)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        string cleaned = sb.ToString().Trim().TrimEnd('.');
        if (cleaned.Length == 0) cleaned = "download";
        if (cleaned.Length > 180) cleaned = cleaned[..180];
        return cleaned;
    }

    /// <summary>
    /// Ekstrak checksum dari header respons.
    /// Mendukung Digest (RFC 3230), Content-MD5, X-Checksum-*/X-File-*,
    /// dan X-Goog-Hash (Google Cloud Storage). Nama header pencarian tidak
    /// perlu divariasikan kapitalisasinya: pencarian header HTTP memang
    /// tidak peka huruf besar-kecil.
    /// </summary>
    public static (string? Algorithm, string? ValueHex) ExtractChecksum(Func<string, string?> getHeader)
    {
        // 1. Coba Digest (RFC 3230); pilih algoritma terkuat yang ditawarkan.
        string? digest = getHeader("Digest");
        if (!string.IsNullOrWhiteSpace(digest))
        {
            var parsed = Storage.Checksum.ParseDigestHeader(digest);
            string[] strength = ["sha512", "sha384", "sha256", "sha1", "md5"];

            foreach (string preferred in strength)
            {
                var match = parsed.FirstOrDefault(p => p.Algorithm == preferred);
                if (match.Algorithm is null) continue;

                try
                {
                    byte[] bytes = Convert.FromBase64String(match.ValueBase64);
                    string hex = Convert.ToHexString(bytes).ToLowerInvariant();
                    return (match.Algorithm, hex);
                }
                catch (FormatException) { }
            }
        }

        // 2. Coba X-Checksum-* atau X-File-* (hex murni, tanpa base64).
        (string Header, string Algorithm, int Length)[] customHeaders =
        [
            ("X-Checksum-SHA256", "sha256", 64),
            ("X-File-SHA256", "sha256", 64),
            ("X-Checksum-SHA1", "sha1", 40),
            ("X-File-SHA1", "sha1", 40),
            ("X-Checksum-MD5", "md5", 32),
            ("X-File-MD5", "md5", 32),
        ];

        foreach (var (header, algorithm, length) in customHeaders)
        {
            string? val = getHeader(header);
            if (string.IsNullOrWhiteSpace(val)) continue;

            string clean = val.Trim().ToLowerInvariant();
            if (clean.Length == length && IsHexString(clean)) return (algorithm, clean);
        }

        // 3. Coba X-Goog-Hash (format "md5=base64,crc32c=base64"; crc32c
        //    tidak didukung, jadi hanya md5 yang dipakai).
        string? goog = getHeader("X-Goog-Hash");
        if (!string.IsNullOrWhiteSpace(goog))
        {
            foreach (string part in goog.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;

                string algorithm = part[..eq].Trim().ToLowerInvariant();
                string body = part[(eq + 1)..].Trim();
                if (algorithm != "md5") continue;

                try
                {
                    byte[] bytes = Convert.FromBase64String(body);
                    return ("md5", Convert.ToHexString(bytes).ToLowerInvariant());
                }
                catch (FormatException) { }
            }
        }

        // 4. Coba Content-MD5 (base64, standar lama).
        string? contentMd5 = getHeader("Content-MD5");
        if (!string.IsNullOrWhiteSpace(contentMd5))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(contentMd5.Trim());
                return ("md5", Convert.ToHexString(bytes).ToLowerInvariant());
            }
            catch (FormatException) { }
        }

        return (null, null);
    }

    private static bool IsHexString(string s)
    {
        foreach (char c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }
}

/// <summary>
/// Rentang byte hasil parse <c>Content-Range</c>. <see cref="End"/> eksklusif.
/// </summary>
public readonly record struct RangeSpan(long Start, long End, long? TotalLength);
