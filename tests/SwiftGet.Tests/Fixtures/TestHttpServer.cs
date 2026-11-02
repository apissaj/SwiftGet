using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SwiftGet.Tests.Fixtures;

/// <summary>
/// Server HTTP minimal di loopback untuk menguji engine tanpa jaringan sungguhan.
/// <para>
/// Cukup pintar untuk meniru perilaku server nyata yang relevan:
/// <c>206 Partial Content</c>, <c>Accept-Ranges</c>, <c>ETag</c>,
/// <c>Content-Disposition</c>, server yang mengabaikan Range, dan 404.
/// </para>
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Tasks.Task _loop;

    public TestHttpServer()
    {
        int port = GetFreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    public Uri BaseUri { get; }

    /// <summary>Isi berkas yang dilayani.</summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>Bila true, server menjawab permintaan Range dengan 200 penuh.</summary>
    public bool IgnoreRanges { get; set; }

    /// <summary>Bila diisi, dikirim sebagai header ETag.</summary>
    public string? ETag { get; set; } = "\"v1\"";

    /// <summary>Bila diisi, dikirim sebagai Content-Disposition.</summary>
    public string? ContentDisposition { get; set; }

    /// <summary>Bila diisi, dikirim sebagai header Digest (RFC 3230).</summary>
    public string? Digest { get; set; }

    /// <summary>Bila diisi, dikirim sebagai header Content-MD5.</summary>
    public string? ContentMd5 { get; set; }

    /// <summary>Bila diisi, dikirim sebagai header X-Checksum-SHA256.</summary>
    public string? ChecksumSha256 { get; set; }

    /// <summary>Bila diisi, dikirim sebagai header X-Goog-Hash.</summary>
    public string? GoogHash { get; set; }

    /// <summary>Bila true, semua permintaan dijawab 404.</summary>
    public bool NotFound { get; set; }

    /// <summary>Jumlah permintaan GET yang diterima (untuk memverifikasi paralelisme).</summary>
    public int GetRequestCount;

    /// <summary>Jumlah permintaan yang membawa header Range.</summary>
    public int RangeRequestCount;

    /// <summary>
    /// Jumlah permintaan data GET yang benar-benar mengirim isi (panjang > 1 byte).
    /// Probe <c>bytes=0-0</c> tidak ikut dihitung, jadi angka ini murni kerja unduh.
    /// </summary>
    public int DataRequestCount;

    /// <summary>
    /// N koneksi data pertama diputus di tengah aliran setelah mengirim sebagian
    /// isi (simulasi server yang bermasalah). Koneksi ke-N+1 dan seterusnya normal.
    /// </summary>
    public int AbortFirstDataConnections { get; set; }

    /// <summary>Berapa byte dikirim sebelum koneksi diputus (harus < panjang rentang).</summary>
    public int AbortAfterBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// N koneksi data pertama dilambatkan: isi dikirim dalam potongan kecil dengan
    /// jeda di antaranya (simulasi koneksi lambat agar worker lain menganggur).
    /// </summary>
    public int SlowFirstDataConnections { get; set; }

    /// <summary>Ukuran potongan untuk koneksi yang dilambatkan.</summary>
    public int SlowChunkBytes { get; set; } = 32 * 1024;

    /// <summary>Jeda antar potongan untuk koneksi yang dilambatkan (ms).</summary>
    public int SlowChunkDelayMs { get; set; } = 5;

    /// <summary>Rentang yang diminta, berurutan.</summary>
    public List<(long From, long To)> RequestedRanges { get; } = new();

    /// <summary>
    /// Routing per-path (AbsolutePath, case-insensitive): path yang terdaftar
    /// dilayani 200 dengan Content-Length yang benar, entah GET maupun HEAD.
    /// Lookup dilakukan paling awal di HandleAsync, sebelum logika Range; header
    /// Range pada path yang terdaftar diabaikan (selalu dijawab 200 penuh).
    /// </summary>
    public Dictionary<string, byte[]> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Content-Type per path untuk Routes; bila tidak diisi, application/octet-stream.</summary>
    public Dictionary<string, string> RouteContentTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// N permintaan GET pertama ke path (AbsolutePath) digagalkan dengan
    /// memutus koneksi lewat <c>res.Abort()</c> sebelum respons dikirim;
    /// permintaan setelah itu dilayani normal.
    /// </summary>
    public Dictionary<string, int> FailFirstPerPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Header Referer dari permintaan GET/HEAD terakhir; null bila permintaan
    /// terakhir tidak membawanya. Semua permintaan ke server yang sama membawa
    /// referer yang sama, jadi nilai ini cukup untuk membuktikan penerusan.
    /// </summary>
    public string? LastReferer { get; private set; }

    /// <summary>
    /// Header Referer terakhir per path (AbsolutePath). Dibutuhkan untuk
    /// unduhan media yang mengambil segmen secara paralel: permintaan terakhir
    /// global bisa berasal dari path mana pun, sedangkan assertion per path
    /// tetap deterministik.
    /// </summary>
    public Dictionary<string, string?> LastRefererPerPath { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Header Cookie dari permintaan GET/HEAD terakhir; null bila permintaan
    /// terakhir tidak membawanya. Sama seperti LastReferer: unduhan membawa
    /// cookie yang sama pada semua permintaan, jadi satu nilai cukup.
    /// </summary>
    public string? LastCookie { get; private set; }

    /// <summary>Catat header Referer permintaan GET/HEAD yang baru masuk.</summary>
    private void CatatReferer(HttpListenerRequest req)
    {
        if (req.HttpMethod is not ("GET" or "HEAD")) return;

        LastReferer = req.Headers["Referer"];
        if (req.Url?.AbsolutePath is string path)
        {
            lock (LastRefererPerPath)
                LastRefererPerPath[path] = LastReferer;
        }
    }

    /// <summary>Catat header Cookie permintaan GET/HEAD yang baru masuk.</summary>
    private void CatatCookie(HttpListenerRequest req)
    {
        if (req.HttpMethod is not ("GET" or "HEAD")) return;

        LastCookie = req.Headers["Cookie"];
    }

    private readonly Dictionary<string, int> _failSisaPerPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _getPerPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _routeGate = new();

    /// <summary>Daftarkan isi statis untuk satu path (AbsolutePath, mis. "/video/seg-1.ts").</summary>
    public void Route(string path, byte[] content, string? contentType = null)
    {
        if (!path.StartsWith('/')) path = "/" + path;
        Routes[path] = content;
        if (contentType is not null) RouteContentTypes[path] = contentType;
    }

    /// <summary>Daftarkan isi teks (UTF-8) untuk satu path.</summary>
    public void RouteText(string path, string text, string? contentType = null) =>
        Route(path, Encoding.UTF8.GetBytes(text), contentType);

    /// <summary>Jumlah permintaan GET yang diterima untuk satu path (AbsolutePath).</summary>
    public int GetRequestCountFor(string path)
    {
        if (!path.StartsWith('/')) path = "/" + path;
        lock (_getPerPath) return _getPerPath.TryGetValue(path, out int n) ? n : 0;
    }

    /// <summary>Hitung apakah permintaan GET ini termasuk jatah gagal FailFirstPerPath.</summary>
    private bool MasukJatahGagal(string path)
    {
        lock (_routeGate)
        {
            if (!FailFirstPerPath.TryGetValue(path, out int jumlah)) return false;
            if (!_failSisaPerPath.TryGetValue(path, out int sisa)) sisa = jumlah;
            if (sisa <= 0) return false;
            _failSisaPerPath[path] = sisa - 1;
            return true;
        }
    }

    /// <summary>
    /// Layani path yang terdaftar di Routes: HEAD dengan Content-Length benar,
    /// GET 200 penuh (Range diabaikan), atau koneksi diputus bila path itu
    /// masih punya jatah di FailFirstPerPath.
    /// </summary>
    private async System.Threading.Tasks.Task ServeRouteAsync(
        HttpListenerRequest req, HttpListenerResponse res, string path, byte[] content)
    {
        string contentType = RouteContentTypes.TryGetValue(path, out string? ct)
            ? ct : "application/octet-stream";

        if (req.HttpMethod == "HEAD")
        {
            res.ContentType = contentType;
            res.ContentLength64 = content.Length;
            res.Close();
            return;
        }

        Interlocked.Increment(ref GetRequestCount);
        lock (_getPerPath)
        {
            _getPerPath.TryGetValue(path, out int n);
            _getPerPath[path] = n + 1;
        }

        if (MasukJatahGagal(path))
        {
            // Putus sebelum satu byte pun dikirim agar klien melihat kegagalan
            // koneksi yang layak dicoba ulang (DownloadException ConnectionFailed).
            // Respons yang terpotong di tengah isi malah muncul sebagai
            // HttpIOException saat isi segmen dibaca — kelas galat yang tidak
            // ditangkap pengambil segmen — dan membuat sesi media menggantung.
            res.Abort();
            return;
        }

        res.StatusCode = 200;
        res.ContentType = contentType;
        res.ContentLength64 = content.Length;
        await res.OutputStream.WriteAsync(content).ConfigureAwait(false);
        res.Close();
    }

    /// <summary>Nomor urut permintaan data; hanya dihitung untuk GET berisi > 1 byte.</summary>
    private int _dataRequestSeq;

    private async System.Threading.Tasks.Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = System.Threading.Tasks.Task.Run(() => HandleAsync(ctx));
        }
    }

    private async System.Threading.Tasks.Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;

            CatatReferer(req);
            CatatCookie(req);

            if (NotFound)
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }

            // Routing per-path diperiksa paling awal, sebelum logika Range.
            string? routePath = req.Url?.AbsolutePath;
            if (routePath is not null && Routes.TryGetValue(routePath, out byte[]? routed))
            {
                await ServeRouteAsync(req, res, routePath, routed).ConfigureAwait(false);
                return;
            }

            string? rangeHeader = req.Headers["Range"];

            if (req.HttpMethod == "HEAD")
            {
                res.AddHeader("Accept-Ranges", "bytes");
                if (ETag is not null) res.AddHeader("ETag", ETag);
                if (ContentDisposition is not null) res.AddHeader("Content-Disposition", ContentDisposition);
                AddChecksumHeaders(res);
                res.ContentType = "application/octet-stream";
                res.ContentLength64 = Content.Length;
                res.Close();
                return;
            }

            Interlocked.Increment(ref GetRequestCount);

            if (rangeHeader is not null) Interlocked.Increment(ref RangeRequestCount);

            // Parse "bytes=start-end" (inklusif), dukung juga "bytes=start-".
            if (rangeHeader is not null && !IgnoreRanges &&
                TryParseRange(rangeHeader, out long from, out long to))
            {
                lock (RequestedRanges) RequestedRanges.Add((from, to));

                if (from >= Content.Length)
                {
                    res.StatusCode = 416;
                    res.AddHeader("Content-Range", $"bytes */{Content.Length}");
                    res.Close();
                    return;
                }

                long actualEnd = Math.Min(to, Content.Length - 1);
                int length = (int)(actualEnd - from + 1);

                // Probe 1 byte tidak dianggap kerja data, jadi tidak kena abort/slow.
                bool isDataRequest = length > 1;
                int seq = 0;
                if (isDataRequest)
                {
                    Interlocked.Increment(ref DataRequestCount);
                    seq = Interlocked.Increment(ref _dataRequestSeq);
                }

                res.StatusCode = 206;
                res.AddHeader("Accept-Ranges", "bytes");
                res.AddHeader("Content-Range", $"bytes {from}-{actualEnd}/{Content.Length}");
                if (ETag is not null) res.AddHeader("ETag", ETag);
                AddChecksumHeaders(res);
                res.ContentType = "application/octet-stream";
                res.ContentLength64 = length;

                if (await CobaPutuskanAtauLambatkanAsync(res, seq, Content, (int)from, length).ConfigureAwait(false))
                    return; // koneksi sudah diputus atau isinya dikirim bertahap

                await res.OutputStream.WriteAsync(Content.AsMemory((int)from, length)).ConfigureAwait(false);
                res.Close();
                return;
            }

            // Respons penuh: entah klien tidak minta Range, atau server mengabaikannya.
            bool dataPenuh = Content.Length > 1;
            int seqPenuh = 0;
            if (dataPenuh)
            {
                Interlocked.Increment(ref DataRequestCount);
                seqPenuh = Interlocked.Increment(ref _dataRequestSeq);
            }

            res.StatusCode = 200;
            res.AddHeader("Accept-Ranges", IgnoreRanges ? "none" : "bytes");
            if (ETag is not null) res.AddHeader("ETag", ETag);
            if (ContentDisposition is not null) res.AddHeader("Content-Disposition", ContentDisposition);
            AddChecksumHeaders(res);
            res.ContentType = "application/octet-stream";
            res.ContentLength64 = Content.Length;

            if (await CobaPutuskanAtauLambatkanAsync(res, seqPenuh, Content, 0, Content.Length).ConfigureAwait(false))
                return;

            await res.OutputStream.WriteAsync(Content).ConfigureAwait(false);
            res.Close();
        }
        catch (HttpListenerException) { /* klien menutup koneksi */ }
        catch (ObjectDisposedException) { /* server ditutup */ }
        catch (IOException) { /* klien memutus di tengah tulis */ }
    }

    /// <summary>
    /// Terapkan perilaku koneksi bermasalah untuk permintaan data ke-<paramref name="seq"/>.
    /// Bila nomor urutnya masuk jatah abort, kirim sebagian isi lalu putuskan koneksi
    /// (klien melihat aliran terpotong sebelum Content-Length terpenuhi). Bila masuk
    /// jatah slow, kirim seluruh isi dalam potongan berjeda. Mengembalikan true bila
    /// respons sudah selesai ditangani di sini.
    /// </summary>
    private async Task<bool> CobaPutuskanAtauLambatkanAsync(
        HttpListenerResponse res, int seq, byte[] content, int offset, int length)
    {
        if (seq <= 0) return false;

        if (seq <= AbortFirstDataConnections && length > AbortAfterBytes)
        {
            await res.OutputStream.WriteAsync(content.AsMemory(offset, AbortAfterBytes)).ConfigureAwait(false);
            await res.OutputStream.FlushAsync().ConfigureAwait(false);
            res.Abort(); // putuskan tanpa melengkapi Content-Length
            return true;
        }

        if (seq <= SlowFirstDataConnections && length > SlowChunkBytes)
        {
            for (int sent = 0; sent < length; sent += SlowChunkBytes)
            {
                int n = Math.Min(SlowChunkBytes, length - sent);
                await res.OutputStream.WriteAsync(content.AsMemory(offset + sent, n)).ConfigureAwait(false);
                await res.OutputStream.FlushAsync().ConfigureAwait(false);
                await Task.Delay(SlowChunkDelayMs).ConfigureAwait(false);
            }
            res.Close();
            return true;
        }

        return false;
    }

    /// <summary>Kirim header checksum opsional yang diisi oleh test.</summary>
    private void AddChecksumHeaders(HttpListenerResponse res)
    {
        if (Digest is not null) res.AddHeader("Digest", Digest);
        if (ContentMd5 is not null) res.AddHeader("Content-MD5", ContentMd5);
        if (ChecksumSha256 is not null) res.AddHeader("X-Checksum-SHA256", ChecksumSha256);
        if (GoogHash is not null) res.AddHeader("X-Goog-Hash", GoogHash);
    }

    private static bool TryParseRange(string header, out long from, out long to)
    {
        from = 0;
        to = long.MaxValue;

        const string prefix = "bytes=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        string spec = header[prefix.Length..].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0) return false;

        if (!long.TryParse(spec[..dash], out from)) return false;

        string endPart = spec[(dash + 1)..].Trim();
        if (endPart.Length > 0 && !long.TryParse(endPart, out to)) return false;

        return true;
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public Uri UrlFor(string name) => new(BaseUri, name);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { await _loop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
