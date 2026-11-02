using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Endpoint HTTP lokal di 127.0.0.1 yang menerima URL dari bookmarklet
/// browser: <c>GET /add?url=...</c> atau <c>POST /add</c> berisi JSON.
/// <para>
/// Hanya mengikat loopback, jadi tidak terjangkau dari jaringan luar.
/// URL yang diterima diberikan ke callback pemiliknya — kelas ini tidak
/// tahu apa-apa tentang unduhan; ia hanya penerima URL dan halaman
/// bookmarklet. Semua kegagalan ditelan: layanan bantu tidak boleh
/// mengganggu aplikasi utama.
/// </para>
/// </summary>
public sealed class LocalEndpointServer : IDisposable
{
    /// <summary>Batas panjang URL, selaras dengan ClipboardWatcher.</summary>
    private const int MaxUrlLength = 2048;

    /// <summary>Batas isi POST supaya koneksi nakal tidak menguras memori.</summary>
    private const int MaxBodyBytes = 16 * 1024;

    private readonly int _port;
    private readonly Func<AddRequest, Task<AddResult>> _add;
    private CancellationTokenSource _cts = new();
    private HttpListener? _listener;
    private Task? _acceptLoop;
    private bool _disposed;

    public LocalEndpointServer(int port, Func<AddRequest, Task<AddResult>> add)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port di luar rentang aman.");
        _port = port;
        _add = add;
    }

    public string BaseUrl => $"http://127.0.0.1:{_port}/";

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Mulai menerima. Melempar <see cref="HttpListenerException"/> bila port
    /// dipakai proses lain; pemanggil yang memutuskan pesannya.
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalEndpointServer));
        if (IsRunning) return;

        // Stop() membatalkan CTS untuk mematikan accept loop lama; siklus Start
        // berikutnya butuh token yang masih hidup, jadi CTS dibuat ulang di sini.
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        IsRunning = true;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _listener?.Close();
        _listener = null;
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                // Tanpa token: HttpListener tidak menerima pembatalan. Berhenti
                // lewat listener.Stop() yang memicu pengecualian di bawah.
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || _listener is not { IsListening: true })
            {
                return; // Stop() atau Dispose() sedang berjalan; bukan galat.
            }
            catch (HttpListenerException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        HttpListenerResponse response = ctx.Response;
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (ctx.Request.HttpMethod == "GET" && (path == "/" || path == ""))
            {
                await WriteAsync(response, 200, BookmarkletPage(), "text/html; charset=utf-8")
                    .ConfigureAwait(false);
                return;
            }

            if (path == "/add")
            {
                await HandleAddAsync(ctx, response, ct).ConfigureAwait(false);
                return;
            }

            await WriteAsync(response, 404, "Tidak ditemukan", "text/plain; charset=utf-8")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Klien menutup koneksi lebih dulu adalah hal biasa; diam saja.
            try { response.Abort(); } catch (Exception) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    private async Task HandleAddAsync(HttpListenerContext ctx, HttpListenerResponse response, CancellationToken ct)
    {
        string? url;
        string? referer;

        switch (ctx.Request.HttpMethod)
        {
            case "GET":
                url = ctx.Request.QueryString["url"];
                referer = ctx.Request.QueryString["referer"];
                break;
            case "POST":
                PostBody body = await ReadPostUrlAsync(ctx.Request, response).ConfigureAwait(false);
                if (body.Handled) return; // 413 sudah terjawab; jangan tulis ulang.
                url = body.Url;
                referer = body.Referer;
                break;
            default:
                url = null;
                referer = null;
                break;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            await WriteAsync(response, 400, "Parameter url kosong", "text/plain; charset=utf-8")
                .ConfigureAwait(false);
            return;
        }

        if (url.Length > MaxUrlLength)
        {
            await WriteAsync(response, 400, "URL terlalu panjang", "text/plain; charset=utf-8")
                .ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            await WriteAsync(response, 400, "URL harus http atau https", "text/plain; charset=utf-8")
                .ConfigureAwait(false);
            return;
        }

        // Referer opsional dari bookmarklet: halaman tempat tautan ditemukan,
        // dipakai server yang mensyaratkan asal permintaan.
        Uri? refererUri = null;
        if (!string.IsNullOrWhiteSpace(referer))
        {
            if (referer.Length > MaxUrlLength ||
                !Uri.TryCreate(referer, UriKind.Absolute, out refererUri) ||
                refererUri.Scheme is not ("http" or "https"))
            {
                await WriteAsync(response, 400, "Referer harus URL http atau https", "text/plain; charset=utf-8")
                    .ConfigureAwait(false);
                return;
            }
        }

        AddResult result = await _add(new AddRequest(parsed.ToString(), refererUri?.ToString()))
            .ConfigureAwait(false);
        int code = result.Ok ? 200 : 400;
        await WriteAsync(response, code, result.Message, "text/plain; charset=utf-8")
            .ConfigureAwait(false);
    }

    /// <summary>Baca body POST berformat <c>{"url": "...", "referer": "..."}</c>.
    /// <see cref="PostBody.Handled"/> true berarti respons sudah ditulis di
    /// sini (413) dan pemanggil tidak menulis lagi.</summary>
    private sealed record PostBody(string? Url, string? Referer, bool Handled);

    private static async Task<PostBody> ReadPostUrlAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        long length = request.ContentLength64;
        if (length is < 0 or > MaxBodyBytes)
        {
            await WriteAsync(response, 413, "Isi terlalu besar", "text/plain; charset=utf-8")
                .ConfigureAwait(false);
            return new PostBody(null, null, Handled: true);
        }

        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync().ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(body);
            string? url = doc.RootElement.TryGetProperty("url", out JsonElement urlElement)
                ? urlElement.GetString()
                : null;
            string? referer = doc.RootElement.TryGetProperty("referer", out JsonElement refererElement)
                ? refererElement.GetString()
                : null;
            return new PostBody(url, referer, Handled: false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new PostBody(null, null, Handled: false);
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int code, string body, string contentType)
    {
        response.StatusCode = code;
        response.ContentType = contentType;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>Halaman penerima bookmarklet: satu tautan yang ditarik ke
    /// bookmark bar. Bookmarklet memindai tautan berkas media dan elemen
    /// video/audio di halaman aktif — bila ada beberapa, pengguna memilih dari
    /// daftar; bila tidak ada, URL halaman dikirim sebagai cadangan. Referer
    /// halaman ikut dikirim untuk server yang mensyaratkannya.</summary>
    private string BookmarkletPage()
    {
        // Skrip ditulis sebagai raw string tanpa interpolasi: JavaScript penuh
        // kurung kurawal, dan menghindarinya butuh pelolosan berlapis yang
        // mudah salah. URL endpoint dimasukkan lewat token.
        const string scriptTemplate = """
            javascript:(function(){
            var ADD='__ADD__';var EXT=/\.(mp4|m4v|mkv|webm|mov|avi|flv|ts|mp3|m4a|wav|flac|ogg|opus|zip|rar|7z|tar|gz|xz|iso|pdf|docx?|xlsx?|pptx?|epub|exe|msi|msix|apk|dmg)([?#]|$)/i;var found=[];var seen={};
            function push(u){try{u=new URL(u,location.href).href;}catch(e){return;}
            if(seen[u])return;if(u.indexOf('__BASE__')===0)return;seen[u]=1;found.push(u);}
            document.querySelectorAll('a[href],video[src],video source[src],audio[src],audio source[src],embed[src],object[data]').forEach(function(el){var u=el.href||el.src||el.data;if(u)push(u);});
            document.querySelectorAll('video,audio').forEach(function(v){try{var c=v.currentSrc||v.src;if(c)push(c);}catch(e){}});
            function send(u){location.href=ADD+'?url='+encodeURIComponent(u)+'&referer='+encodeURIComponent(location.href);}
            if(found.length===1){send(found[0]);return;}
            var d=document.createElement('div');d.style.cssText='position:fixed;inset:0;background:rgba(0,0,0,.5);z-index:2147483647;font:14px/1.5 system-ui,sans-serif;';
            var b=document.createElement('div');b.style.cssText='position:absolute;top:8%;left:50%;transform:translateX(-50%);background:#fff;color:#1a1a1a;border-radius:10px;padding:14px 18px;width:min(640px,92vw);max-height:74vh;overflow:auto;box-shadow:0 12px 40px rgba(0,0,0,.4);';
            var h=document.createElement('div');h.textContent='Kirim ke SwiftGet — '+found.length+' kandidat di '+location.hostname;h.style.cssText='font-weight:600;margin-bottom:8px;';b.appendChild(h);
            if(found.length===0){var e=document.createElement('div');e.textContent='Tidak ada tautan berkas media di halaman ini. URL halaman dikirim sebagai cadangan.';e.style.cssText='margin-bottom:8px;';b.appendChild(e);}
            found.forEach(function(u){var a=document.createElement('a');a.href='#';a.textContent=u;a.style.cssText='display:block;padding:6px 2px;word-break:break-all;color:#0a7a5c;text-decoration:none;border-top:1px solid #eee;';a.onclick=function(ev){ev.preventDefault();document.body.removeChild(d);send(u);};b.appendChild(a);});
            if(found.length===0){var s=document.createElement('a');s.href='#';s.textContent='Kirim URL halaman ini';s.style.cssText='display:inline-block;margin-top:8px;padding:6px 12px;border:1px solid #bbb;border-radius:6px;color:#1a1a1a;text-decoration:none;';s.onclick=function(ev){ev.preventDefault();document.body.removeChild(d);send(location.href);};b.appendChild(s);}
            var x=document.createElement('a');x.href='#';x.textContent='Tutup';x.style.cssText='display:inline-block;margin:10px 0 0;padding:6px 12px;border:1px solid #bbb;border-radius:6px;color:#1a1a1a;text-decoration:none;';x.onclick=function(ev){ev.preventDefault();document.body.removeChild(d);};b.appendChild(x);
            d.appendChild(b);document.body.appendChild(d);})()
            """;

        string script = scriptTemplate
            .Replace("__ADD__", $"{BaseUrl}add")
            .Replace("__BASE__", BaseUrl);

        // Skrip sengaja hanya memakai kutip tunggal dan tanpa < > & " supaya
        // aman langsung masuk atribut href tanpa HtmlEncode — encode justru
        // mengubah kutip tunggal menjadi &#39; dan merusak skrip saat ditarik
        // ke bookmark bar di sebagian browser.
        string encoded = script;

        return $"""
            <!doctype html>
            <html lang="id">
            <head><meta charset="utf-8"><title>SwiftGet</title></head>
            <body style="font-family: system-ui, sans-serif; max-width: 34rem; margin: 3rem auto; line-height: 1.6;">
              <h1 style="font-size: 1.4rem;">SwiftGet siap menerima unduhan</h1>
              <p>Tarik tombol ini ke bookmark bar browser Anda, lalu klik saat berada di halaman yang memuat berkas media. Bookmarklet memindai tautan berkas dan elemen video/audio di halaman, lalu Anda memilih yang mau diunduh:</p>
              <p><a href="{encoded}"
                    style="display: inline-block; padding: .5rem 1rem; border: 1px solid #999; border-radius: 6px; text-decoration: none;">
                    Kirim ke SwiftGet</a></p>
              <p style="color: #666;">Referer halaman ikut dikirim. Endpoint hanya aktif di komputer ini. Cookie privat browser tidak bisa dibaca bookmarklet — untuk berkas yang mewajibkan login, unduhan mungkin ditolak server.</p>
            </body>
            </html>
            """;
    }

    /// <summary>Hasil penerimaan URL: ok dan nama berkas/pesan untuk browser.</summary>
    public sealed record AddResult(bool Ok, string Message);

    /// <summary>URL yang diterima endpoint, beserta referer halaman asalnya bila ada.</summary>
    public sealed record AddRequest(string Url, string? Referer);
}
