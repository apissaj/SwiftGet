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
    private readonly Func<string, Task<AddResult>> _add;
    private CancellationTokenSource _cts = new();
    private HttpListener? _listener;
    private Task? _acceptLoop;
    private bool _disposed;

    public LocalEndpointServer(int port, Func<string, Task<AddResult>> add)
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

        switch (ctx.Request.HttpMethod)
        {
            case "GET":
                url = ctx.Request.QueryString["url"];
                break;
            case "POST":
                PostBody body = await ReadPostUrlAsync(ctx.Request, response).ConfigureAwait(false);
                if (body.Handled) return; // 413 sudah terjawab; jangan tulis ulang.
                url = body.Url;
                break;
            default:
                url = null;
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

        AddResult result = await _add(parsed.ToString()).ConfigureAwait(false);
        int code = result.Ok ? 200 : 400;
        await WriteAsync(response, code, result.Message, "text/plain; charset=utf-8")
            .ConfigureAwait(false);
    }

    /// <summary>Baca body POST berformat <c>{"url": "..."}</c>.
    /// <see cref="PostBody.Handled"/> true berarti respons sudah ditulis di
    /// sini (413) dan pemanggil tidak menulis lagi.</summary>
    private sealed record PostBody(string? Url, bool Handled);

    private static async Task<PostBody> ReadPostUrlAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        long length = request.ContentLength64;
        if (length is < 0 or > MaxBodyBytes)
        {
            await WriteAsync(response, 413, "Isi terlalu besar", "text/plain; charset=utf-8")
                .ConfigureAwait(false);
            return new PostBody(null, Handled: true);
        }

        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync().ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(body);
            string? url = doc.RootElement.TryGetProperty("url", out JsonElement urlElement)
                ? urlElement.GetString()
                : null;
            return new PostBody(url, Handled: false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new PostBody(null, Handled: false);
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

    /// <summary>
    /// Halaman penerima bookmarklet: satu tautan yang ditarik ke bookmark bar.
    /// Halaman ini juga konfirmasi hidup bahwa endpoint berjalan.
    /// </summary>
    private string BookmarkletPage()
    {
        string script = $"javascript:location.href='{BaseUrl}add?url='+encodeURIComponent(location.href)";
        return $"""
            <!doctype html>
            <html lang="id">
            <head><meta charset="utf-8"><title>SwiftGet</title></head>
            <body style="font-family: system-ui, sans-serif; max-width: 34rem; margin: 3rem auto; line-height: 1.6;">
              <h1 style="font-size: 1.4rem;">SwiftGet siap menerima unduhan</h1>
              <p>Tarik tombol ini ke bookmark bar browser Anda, lalu klik saat ingin mengunduh berkas di halaman mana pun:</p>
              <p><a href="{System.Net.WebUtility.HtmlEncode(script)}"
                    style="display: inline-block; padding: .5rem 1rem; border: 1px solid #999; border-radius: 6px; text-decoration: none;">
                    Kirim ke SwiftGet</a></p>
              <p style="color: #666;">Bookmarklet membuka halaman kecil ini sebagai konfirmasi. URL aktif hanya di komputer ini.</p>
            </body>
            </html>
            """;
    }

    /// <summary>Hasil penerimaan URL: ok dan nama berkas/pesan untuk browser.</summary>
    public sealed record AddResult(bool Ok, string Message);
}
