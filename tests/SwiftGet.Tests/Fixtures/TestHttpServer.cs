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

    /// <summary>Rentang yang diminta, berurutan.</summary>
    public List<(long From, long To)> RequestedRanges { get; } = new();

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

            if (NotFound)
            {
                res.StatusCode = 404;
                res.Close();
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

                res.StatusCode = 206;
                res.AddHeader("Accept-Ranges", "bytes");
                res.AddHeader("Content-Range", $"bytes {from}-{actualEnd}/{Content.Length}");
                if (ETag is not null) res.AddHeader("ETag", ETag);
                AddChecksumHeaders(res);
                res.ContentType = "application/octet-stream";
                res.ContentLength64 = length;

                await res.OutputStream.WriteAsync(Content.AsMemory((int)from, length)).ConfigureAwait(false);
                res.Close();
                return;
            }

            // Respons penuh: entah klien tidak minta Range, atau server mengabaikannya.
            res.StatusCode = 200;
            res.AddHeader("Accept-Ranges", IgnoreRanges ? "none" : "bytes");
            if (ETag is not null) res.AddHeader("ETag", ETag);
            if (ContentDisposition is not null) res.AddHeader("Content-Disposition", ContentDisposition);
            AddChecksumHeaders(res);
            res.ContentType = "application/octet-stream";
            res.ContentLength64 = Content.Length;

            await res.OutputStream.WriteAsync(Content).ConfigureAwait(false);
            res.Close();
        }
        catch (HttpListenerException) { /* klien menutup koneksi */ }
        catch (ObjectDisposedException) { /* server ditutup */ }
        catch (IOException) { /* klien memutus di tengah tulis */ }
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
