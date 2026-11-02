using System.Net;
using SwiftGet.Core.Downloads;

namespace SwiftGet.Core.Http;

/// <summary>
/// Klien HTTP yang khusus menangani unduhan ber-Range.
/// <para>
/// Tanggung jawabnya sengaja sempit: probe URL dan membuka stream untuk satu
/// rentang byte tertentu. Semua penulisan file ada di <c>PartFile</c>, semua
/// orkestrasi ada di <c>DownloadSession</c>.
/// </para>
/// </summary>
public sealed class HttpRangeClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private bool _disposed;

    public HttpRangeClient(HttpClient? client = null, string? userAgent = null)
    {
        if (client is null)
        {
            var handler = new SocketsHttpHandler
            {
                // Jangan pakai cache otomatis: kita butuh rentang byte eksak.
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                // Redirect ditangani manual supaya kita tahu URL final-nya.
                AllowAutoRedirect = false,
                MaxConnectionsPerServer = 64,
            };

            _client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan, // timeout per operasi, bukan per request
            };
            _ownsClient = true;
        }
        else
        {
            _client = client;
            _ownsClient = false;
        }

        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            userAgent ?? "SwiftGet/0.1 (+https://localhost/swiftget)");
    }

    /// <summary>
    /// Periksa URL: panjang konten, dukungan Range, validator, dan nama file.
    /// <para>
    /// Strategi: kirim <c>HEAD</c> lebih dulu. Bila server menolak HEAD (405/501)
    /// atau tidak memberi info yang cukup, jatuhkan ke <c>GET</c> dengan
    /// <c>Range: bytes=0-0</c> yang membaca 1 byte saja.
    /// </para>
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(Uri uri, CancellationToken ct = default, string? referer = null, string? cookies = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ValidateScheme(uri);

        Uri current = uri;
        for (int hop = 0; hop <= 10; hop++)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, current);
            using HttpResponseMessage resp = await SendAsync(head, ct, referer: referer, cookies: cookies).ConfigureAwait(false);

            if (IsRedirect(resp.StatusCode))
            {
                current = ResolveRedirect(current, resp);
                continue;
            }

            // HEAD berhasil dan server memberi panjang -> cukup.
            if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
            {
                long? len = resp.Content.Headers.ContentLength;
                bool accepts = HeaderParsing.AcceptsByteRanges(
                    resp.Headers.TryGetValues("Accept-Ranges", out var ar) ? string.Join(",", ar) : null);

                // Header Accept-Ranges saja TIDAK cukup untuk menyimpulkan dukungan
                // Range: banyak server mengirim "bytes" tetapi mengabaikan header
                // Range. Satu-satunya bukti kuat adalah respons 206 terhadap
                // permintaan Range. Jadi selalu konfirmasi lewat permintaan 1 byte.
                if (resp.StatusCode == HttpStatusCode.PartialContent && len is > 0)
                {
                    return BuildProbe(current, resp, len, supportsRanges: true);
                }

                // HEAD memberi panjang tetapi bukan bukti Range: konfirmasi dulu.
                // Nama file dari Content-Disposition ikut dibawa karena respons 206
                // biasanya tidak menyertakannya kembali.
                string? headName = HeaderParsing.ParseContentDispositionFileName(
                    resp.Content.Headers.TryGetValues("Content-Disposition", out var hcd)
                        ? string.Join(";", hcd) : null);
                ProbeResult? confirmed = await ProbeWithRangeAsync(current, ct, headName, referer, cookies).ConfigureAwait(false);
                if (confirmed is not null) return confirmed;

                // Tidak mendukung Range. Pakai panjang dari HEAD bila ada.
                return BuildProbe(current, resp, len, supportsRanges: false);
            }

            // HEAD ditolak (405/501) atau aneh -> coba GET 1 byte.
            if (resp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                or HttpStatusCode.Forbidden)
            {
                var ranged = await ProbeWithRangeAsync(current, ct, referer: referer, cookies: cookies).ConfigureAwait(false);
                if (ranged is not null) return ranged;

                // GET tanpa Range sebagai upaya terakhir untuk mendapat metadata.
                using var get = new HttpRequestMessage(HttpMethod.Get, current);
                using HttpResponseMessage getResp = await SendAsync(get, ct, referer: referer, cookies: cookies)
                    .ConfigureAwait(false);

                if (IsRedirect(getResp.StatusCode))
                {
                    current = ResolveRedirect(current, getResp);
                    continue;
                }

                EnsureSuccess(getResp, current);
                long? getLen = getResp.Content.Headers.ContentLength;
                return BuildProbe(current, getResp, getLen, supportsRanges: false);
            }

            EnsureSuccess(resp, current);
            long? fallbackLen = resp.Content.Headers.ContentLength;
            return BuildProbe(current, resp, fallbackLen, supportsRanges: false);
        }

        throw new DownloadException(DownloadErrorKind.HttpStatus,
            $"Terlalu banyak redirect saat memeriksa {uri}.");
    }

    /// <summary>
    /// Buka respons <c>206 Partial Content</c> untuk rentang yang diminta.
    /// <para>
    /// Pemanggil bertanggung jawab membuang <see cref="HttpResponseMessage"/>.
    /// Bila server mengabaikan Range dan mengirim <c>200 OK</c> penuh, hasil
    /// menandai itu lewat <see cref="RangedResponse.RangeIgnored"/>.
    /// </para>
    /// </summary>
    public async Task<RangedResponse> GetRangeAsync(
        Uri uri, long fromInclusive, long toInclusive, CancellationToken ct = default,
        string? referer = null, string? cookies = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ValidateScheme(uri);
        if (toInclusive < fromInclusive)
            throw new ArgumentOutOfRangeException(nameof(toInclusive));

        Uri current = uri;
        for (int hop = 0; hop <= 10; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation(
                "Range", HeaderParsing.BuildRangeHeader(fromInclusive, toInclusive));
            // Beberapa CDN memakai kompresi yang merusak kesetaraan byte; minta identitas.
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

            HttpResponseMessage resp = await SendAsync(request, ct, HttpCompletionOption.ResponseHeadersRead, referer, cookies)
                .ConfigureAwait(false);

            if (IsRedirect(resp.StatusCode))
            {
                // Resolve dulu, baru dispose: setelah Dispose header tidak boleh dibaca.
                current = ResolveRedirect(current, resp);
                resp.Dispose();
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                var span = HeaderParsing.ParseContentRange(
                    resp.Content.Headers.TryGetValues("Content-Range", out var cr)
                        ? string.Join(",", cr) : null);

                if (span is null || span.Value.Start != fromInclusive)
                {
                    resp.Dispose();
                    throw new DownloadException(DownloadErrorKind.CorruptResponse,
                        $"Server menjawab 206 tetapi Content-Range tidak sesuai. " +
                        $"Diminta mulai {fromInclusive}, diterima '{span}'.");
                }

                return new RangedResponse(resp, rangeHonored: true);
            }

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                // Server mengabaikan Range. Pemanggil yang memutuskan fallback.
                return new RangedResponse(resp, rangeHonored: false);
            }

            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                resp.Dispose();
                throw new DownloadException(DownloadErrorKind.HttpStatus,
                    $"Server menolak rentang {fromInclusive}-{toInclusive} (416). " +
                    "Kemungkinan file sudah berubah ukuran.");
            }

            EnsureSuccess(resp, current);
            resp.Dispose();
            throw new DownloadException(DownloadErrorKind.HttpStatus,
                $"Status tak terduga {(int)resp.StatusCode} untuk {current}.");
        }

        throw new DownloadException(DownloadErrorKind.HttpStatus,
            $"Terlalu banyak redirect saat mengunduh {uri}.");
    }

    /// <summary>
    /// GET penuh untuk berkas kecil seperti playlist media. Isi di-stream,
    /// bukan dimuat ke memori oleh klien ini. Redirect diikuti manual dengan
    /// aturan yang sama seperti jalur Range.
    /// </summary>
    public async Task<FullResponse> GetAsync(Uri uri, CancellationToken ct = default, string? referer = null, string? cookies = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ValidateScheme(uri);

        Uri current = uri;
        for (int hop = 0; hop <= 10; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

            HttpResponseMessage resp = await SendAsync(request, ct, HttpCompletionOption.ResponseHeadersRead, referer, cookies)
                .ConfigureAwait(false);

            if (IsRedirect(resp.StatusCode))
            {
                current = ResolveRedirect(current, resp);
                resp.Dispose();
                continue;
            }

            EnsureSuccess(resp, current);
            return new FullResponse(resp, current);
        }

        throw new DownloadException(DownloadErrorKind.HttpStatus,
            $"Terlalu banyak redirect saat mengambil {uri}.");
    }

    private async Task<ProbeResult?> ProbeWithRangeAsync(
        Uri uri, CancellationToken ct, string? fallbackFileName = null,
        string? referer = null, string? cookies = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        using HttpResponseMessage resp = await SendAsync(request, ct, referer: referer, cookies: cookies)
            .ConfigureAwait(false);

        // 200 OK berarti server MENGABAIKAN Range, jadi tidak bisa dipakai sebagai
        // bukti dukungan. Ini kasus umum pada server yang tidak paham Range.
        if (resp.StatusCode != HttpStatusCode.PartialContent) return null;

        var span = HeaderParsing.ParseContentRange(
            resp.Content.Headers.TryGetValues("Content-Range", out var cr) ? string.Join(",", cr) : null);

        long? total = span?.TotalLength;

        // Pengaman tambahan: kalau header Content-Range tidak ada / tidak valid,
        // jangan menyimpulkan dukungan Range dari status saja.
        if (span is null) return null;

        // Respons terhadap "bytes=0-0" harus benar-benar 1 byte. Bila server
        // mengirim lebih banyak, Content-Range tidak bisa dipercaya.
        if (resp.Content.Headers.ContentLength is > 1) return null;

        ProbeResult probe = BuildProbe(uri, resp, total, supportsRanges: true);
        return fallbackFileName is null || probe.FileNameFromServer
            ? probe
            : probe.WithFallbackFileName(fallbackFileName);
    }

    private static ProbeResult BuildProbe(
        Uri uri, HttpResponseMessage resp, long? totalLength, bool supportsRanges)
    {
        // Kumpulkan nama file dari Content-Disposition. Respons probe sering tidak
        // membawa header ini, jadi nama yang sudah ada di task tidak ditimpa.
        string? disposition = resp.Content.Headers.TryGetValues("Content-Disposition", out var cd)
            ? string.Join(";", cd) : null;
        string? dispositionName = null;

        // GET 206 tidak selalu membawa Content-Disposition; ambil dari respons
        // sebelumnya (HEAD/GET penuh) bila ada. Disimpan per-probe oleh pemanggil.
        if (disposition is not null)
            dispositionName = HeaderParsing.ParseContentDispositionFileName(disposition);

        string? etag = resp.Headers.ETag?.Tag ?? (resp.Headers.TryGetValues("ETag", out var e)
            ? string.Join(",", e) : null);

        string? lastModified = resp.Content.Headers.TryGetValues("Last-Modified", out var lm)
            ? string.Join(",", lm)
            : resp.Headers.TryGetValues("Last-Modified", out var lm2) ? string.Join(",", lm2) : null;

        string? GetHeader(string name)
        {
            if (resp.Headers.TryGetValues(name, out var values))
                return string.Join(",", values);
            if (resp.Content?.Headers.TryGetValues(name, out var cValues) == true)
                return string.Join(",", cValues);
            return null;
        }

        var (checksumAlgo, checksumVal) = HeaderParsing.ExtractChecksum(GetHeader);

        return new ProbeResult
        {
            FinalUri = uri,
            ContentLength = totalLength,
            SupportsRanges = supportsRanges,
            ETag = etag,
            LastModifiedRaw = lastModified,
            SuggestedFileName = dispositionName,
            ContentType = resp.Content.Headers.ContentType?.MediaType,
            ExpectedChecksumAlgorithm = checksumAlgo,
            ExpectedChecksumValue = checksumVal,
        };
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption option = HttpCompletionOption.ResponseHeadersRead,
        string? referer = null, string? cookies = null)
    {
        // Asal dan sesi halaman dari ekstensi/bookmarklet; ditempel di semua
        // hop redirect. Cookie dikirim sebagai satu header utuh.
        if (!string.IsNullOrEmpty(referer))
            request.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(cookies))
            request.Headers.TryAddWithoutValidation("Cookie", cookies);

        try
        {
            return await _client.SendAsync(request, option, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new DownloadException(DownloadErrorKind.Canceled, "Operasi dibatalkan.", new OperationCanceledException());
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException(DownloadErrorKind.ConnectionFailed,
                $"Gagal terhubung: {ex.Message}", ex);
        }
    }

    private static void ValidateScheme(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            throw new DownloadException(DownloadErrorKind.InvalidUrl, $"URL tidak absolut: {uri}");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new DownloadException(DownloadErrorKind.InvalidUrl,
                $"Skema '{uri.Scheme}' tidak didukung. Hanya http dan https.");
    }

    private static bool IsRedirect(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static Uri ResolveRedirect(Uri current, HttpResponseMessage resp)
    {
        Uri? location = resp.Headers.Location;
        if (location is null)
            throw new DownloadException(DownloadErrorKind.HttpStatus,
                $"Server mengirim redirect tanpa header Location untuk {current}.");
        return location.IsAbsoluteUri ? location : new Uri(current, location);
    }

    private static void EnsureSuccess(HttpResponseMessage resp, Uri uri)
    {
        if (resp.IsSuccessStatusCode) return;

        var kind = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DownloadErrorKind.AccessDenied,
            HttpStatusCode.NotFound or HttpStatusCode.Gone => DownloadErrorKind.HttpStatus,
            _ => DownloadErrorKind.HttpStatus,
        };

        throw new DownloadException(kind,
            $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}) untuk {uri}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }
}

/// <summary>
/// Respons untuk satu permintaan rentang. Pemilik wajib membuang <see cref="Response"/>.
/// </summary>
public sealed class RangedResponse : IDisposable
{
    public RangedResponse(HttpResponseMessage response, bool rangeHonored)
    {
        Response = response;
        RangeHonored = rangeHonored;
    }

    public HttpResponseMessage Response { get; }

    /// <summary>True bila server menjawab 206 dan menghormati rentang yang diminta.</summary>
    public bool RangeHonored { get; }

    public Stream GetStream() => Response.Content.ReadAsStream();

    public void Dispose() => Response.Dispose();
}

/// <summary>
/// Respons GET penuh: isi media (playlist, segmen) dibaca stream oleh
/// pemanggil. <see cref="FinalUri"/> dipakai untuk meresolusi URL relatif
/// di dalam playlist.
/// </summary>
public sealed class FullResponse : IDisposable
{
    public FullResponse(HttpResponseMessage response, Uri finalUri)
    {
        Response = response;
        FinalUri = finalUri;
    }

    public HttpResponseMessage Response { get; }

    public Uri FinalUri { get; }

    public string ContentType => Response.Content.Headers.ContentType?.MediaType ?? "";

    public Stream GetStream() => Response.Content.ReadAsStream();

    public void Dispose() => Response.Dispose();
}



