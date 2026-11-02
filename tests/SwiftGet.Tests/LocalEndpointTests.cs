using System.Net;
using System.Net.Sockets;
using System.Text;
using SwiftGet.App.Infrastructure;

namespace SwiftGet.Tests;

/// <summary>
/// Uji LocalEndpointServer sebagai penerima URL bookmarklet: halaman penerima,
/// validasi GET/POST /add, batas ukuran, dan siklus hidup listener.
/// <para>
/// Semua test memakai port bebas di 127.0.0.1 dengan callback rekam sederhana,
/// jadi tidak menyentuh jaringan luar maupun instance aplikasi yang sedang jalan.
/// </para>
/// </summary>
public sealed class LocalEndpointTests : IDisposable
{
    private LocalEndpointServer? _server;
    private readonly List<LocalEndpointServer.AddRequest> _requestKeCallback = new();

    /// <summary>Diisi test yang butuh menunggu request persis seperti sampai ke callback.</summary>
    private TaskCompletionSource<LocalEndpointServer.AddRequest>? _requestTerakhir;

    public void Dispose() => _server?.Dispose();

    // ---- Perangkat bantu ----

    private static HttpClient KlienBaru() => new(new SocketsHttpHandler
    {
        // Endpoint lokal: jangan lewat proxy sistem, mesin pengembang sering berproxy.
        UseProxy = false,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    private static int AmbilPortBebas()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Server yang sudah berjalan di port bebas. Port hasil TcpListener bisa
    /// keburu direbut proses lain sebelum HttpListener memakainya, jadi bila
    /// Start kena AddressInUse, dicoba lagi dengan port lain (maks 5 kali).
    /// </summary>
    private LocalEndpointServer MulaiServer(Func<LocalEndpointServer.AddRequest, Task<LocalEndpointServer.AddResult>>? add = null)
    {
        add ??= CatatDanTerima;
        Exception? terakhir = null;

        for (int percobaan = 1; percobaan <= 5; percobaan++)
        {
            var server = new LocalEndpointServer(AmbilPortBebas(), add);
            try
            {
                server.Start();
                _server = server;
                return server;
            }
            catch (HttpListenerException ex)
            {
                server.Dispose();
                terakhir = ex;
            }
        }

        throw new InvalidOperationException("Lima kali gagal mendapat port bebas.", terakhir);
    }

    private Task<LocalEndpointServer.AddResult> CatatDanTerima(LocalEndpointServer.AddRequest request)
    {
        lock (_requestKeCallback) _requestKeCallback.Add(request);
        _requestTerakhir?.TrySetResult(request);
        return Task.FromResult(new LocalEndpointServer.AddResult(true, $"Diterima: {request.Url}"));
    }

    private Task<LocalEndpointServer.AddResult> TolakDenganPesan(LocalEndpointServer.AddRequest request) =>
        Task.FromResult(new LocalEndpointServer.AddResult(false, "URL tidak valid: host tidak bisa diurai"));

    private static string UrlAdd(string baseUrl, string url, string? referer = null)
    {
        string hasil = $"{baseUrl}add?url={Uri.EscapeDataString(url)}";
        if (referer is not null) hasil += $"&referer={Uri.EscapeDataString(referer)}";
        return hasil;
    }

    // ---- Halaman bookmarklet ----

    [Fact]
    public async Task HalamanRoot_MenampilkanTautanBookmarklet()
    {
        var server = MulaiServer();
        int port = new Uri(server.BaseUrl).Port;

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(server.BaseUrl);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("text/html", resp.Content.Headers.ContentType?.ToString());
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("SwiftGet siap menerima unduhan", body);
        // URL endpoint masuk ke variabel ADD skrip; referer dikirim terpisah.
        Assert.Contains($"var ADD='http://127.0.0.1:{port}/add';", body);
        Assert.Contains("referer=", body);
        // Skrip deteksi: dialog pilihan kandidat dan pesan cadangan tanpa kandidat.
        Assert.Contains("Kirim ke SwiftGet — ", body);
        Assert.Contains("Tidak ada tautan berkas media", body);
    }

    // ---- GET /add ----

    [Fact]
    public async Task Add_GetValid_MeneruskanUrlTernormalisasiKeCallback()
    {
        _requestTerakhir = new TaskCompletionSource<LocalEndpointServer.AddRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(UrlAdd(server.BaseUrl, "http://contoh/file.zip"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var diterima = await _requestTerakhir.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("http://contoh/file.zip", diterima.Url);
        Assert.Equal($"Diterima: {diterima.Url}", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Add_CallbackGagal_PesanDiteruskanDengan400()
    {
        var server = MulaiServer(TolakDenganPesan);

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(UrlAdd(server.BaseUrl, "https://contoh/a.zip"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("URL tidak valid: host tidak bisa diurai", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Add_TanpaParameterUrl_Ditolak400TanpaCallback()
    {
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(server.BaseUrl + "add");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Parameter url kosong", await resp.Content.ReadAsStringAsync());
        lock (_requestKeCallback) Assert.Empty(_requestKeCallback);
    }

    [Theory]
    [InlineData("ftp://x/file")]
    [InlineData("bukan url")]
    public async Task Add_UrlBukanHttpHttps_Ditolak400(string url)
    {
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(UrlAdd(server.BaseUrl, url));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("URL harus http atau https", await resp.Content.ReadAsStringAsync());
        lock (_requestKeCallback) Assert.Empty(_requestKeCallback);
    }

    [Fact]
    public async Task Add_UrlMelebihiBatas_Ditolak400()
    {
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(UrlAdd(server.BaseUrl, new string('a', 2100)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("URL terlalu panjang", await resp.Content.ReadAsStringAsync());
        lock (_requestKeCallback) Assert.Empty(_requestKeCallback);
    }

    // ---- Referer pada /add ----

    [Fact]
    public async Task Add_Get_DenganReferer_CallbackMenerimaUrlDanReferer()
    {
        _requestTerakhir = new TaskCompletionSource<LocalEndpointServer.AddRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(
            UrlAdd(server.BaseUrl, "http://contoh/a.zip", "https://halaman/asal"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var request = await _requestTerakhir.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("http://contoh/a.zip", request.Url);
        Assert.Equal("https://halaman/asal", request.Referer);
    }

    [Fact]
    public async Task Add_Get_TanpaReferer_RefererCallbackNull()
    {
        _requestTerakhir = new TaskCompletionSource<LocalEndpointServer.AddRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(UrlAdd(server.BaseUrl, "http://contoh/a.zip"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var request = await _requestTerakhir.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("http://contoh/a.zip", request.Url);
        Assert.Null(request.Referer);
    }

    [Fact]
    public async Task Add_PostReferer_DiteruskanKeCallback()
    {
        _requestTerakhir = new TaskCompletionSource<LocalEndpointServer.AddRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var isi = new StringContent(
            """{"url":"https://contoh/a.zip","referer":"https://halaman/asal"}""",
            Encoding.UTF8, "application/json");
        using var resp = await klien.PostAsync(server.BaseUrl + "add", isi);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var request = await _requestTerakhir.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("https://contoh/a.zip", request.Url);
        Assert.Equal("https://halaman/asal", request.Referer);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("bukan url")]
    public async Task Add_RefererBukanHttp_Ditolak400(string referer)
    {
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(
            UrlAdd(server.BaseUrl, "https://contoh/a.zip", referer));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Referer harus URL http atau https", await resp.Content.ReadAsStringAsync());
        lock (_requestKeCallback) Assert.Empty(_requestKeCallback);
    }

    [Fact]
    public async Task Add_RefererTerlaluPanjang_Ditolak400()
    {
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(
            UrlAdd(server.BaseUrl, "https://contoh/a.zip", new string('a', 2100)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Referer harus URL http atau https", await resp.Content.ReadAsStringAsync());
        lock (_requestKeCallback) Assert.Empty(_requestKeCallback);
    }

    // ---- POST /add ----

    [Fact]
    public async Task Add_PostValid_MeneruskanUrlKeCallback()
    {
        _requestTerakhir = new TaskCompletionSource<LocalEndpointServer.AddRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var isi = new StringContent("""{"url":"https://contoh/a.zip"}""", Encoding.UTF8, "application/json");
        using var resp = await klien.PostAsync(server.BaseUrl + "add", isi);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var diterima = await _requestTerakhir.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("https://contoh/a.zip", diterima.Url);
        Assert.Null(diterima.Referer);
    }

    [Fact]
    public async Task Add_PostBodyMelebihiBatas_Ditolak413()
    {
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var isi = new ByteArrayContent(new byte[20 * 1024]);
        using var resp = await klien.PostAsync(server.BaseUrl + "add", isi);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Contains("Isi terlalu besar", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("bukan json")]
    [InlineData("{\"bukan\":\"url\"}")]
    public async Task Add_PostJsonTidakValid_Ditolak400(string body)
    {
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var isi = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await klien.PostAsync(server.BaseUrl + "add", isi);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Parameter url kosong", await resp.Content.ReadAsStringAsync());
    }

    // ---- Route lain ----

    [Fact]
    public async Task PathLain_Menjawab404()
    {
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(server.BaseUrl + "halaman-lain");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("Tidak ditemukan", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Add_MethodAsing_JatuhKeValidasiUrlKosong()
    {
        // Method selain GET/POST tidak punya jalur khusus di /add: url dianggap
        // kosong sehingga hasilnya 400 validasi, bukan 404. Dikunci di sini agar
        // kalau perilakunya nanti diubah, perubahannya sadar dan disengaja.
        var server = MulaiServer();

        using var klien = KlienBaru();
        using var pesan = new HttpRequestMessage(HttpMethod.Delete, server.BaseUrl + "add");
        using var resp = await klien.SendAsync(pesan);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Parameter url kosong", await resp.Content.ReadAsStringAsync());
    }

    // ---- Konfigurasi dan siklus hidup ----

    [Theory]
    [InlineData(80)]
    [InlineData(70000)]
    public void Konstruktor_PortDiLuarRentang_Melempar(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocalEndpointServer(port, CatatDanTerima));
    }

    [Fact]
    public async Task StartUlang_SetelahStop_MasihMelayani()
    {
        var server = MulaiServer();

        server.Stop();
        server.Start();

        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(server.BaseUrl);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public void Dispose_DuaKali_TidakMelemparDanBerhenti()
    {
        var server = MulaiServer();

        server.Dispose();
        server.Dispose();

        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task Start_DuaKali_TidakMelemparDanTetapMelayani()
    {
        var server = MulaiServer();

        server.Start();

        Assert.True(server.IsRunning);
        using var klien = KlienBaru();
        using var resp = await klien.GetAsync(server.BaseUrl);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
