using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Pemantau clipboard berbasis Win32 (<c>AddClipboardFormatListener</c>).
/// <para>
/// Memakai notifikasi event, bukan polling, sehingga tidak membebani CPU saat
/// idle. Bendera default diambil dari aplikasi untuk memastikan urutan handler
/// dengan WPF sudah benar.
/// </para>
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _disposed;

    public ClipboardWatcher(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>Dipicu saat clipboard berisi teks yang tampak seperti URL unduhan.</summary>
    public event Action<string>? UrlDetected;

    public bool Enabled { get; set; } = true;

    /// <summary>URL terakhir yang sudah diproses, untuk mencegah pemrosesan ganda.</summary>
    private string? _lastUrl;

    private static readonly string[] DownloadExtensions =
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso",
        ".exe", ".msi", ".msix", ".appx", ".dmg", ".pkg",
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".ts", ".m4v",
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus",
        ".pdf", ".epub", ".mobi", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".apk", ".jar", ".bin", ".img", ".deb", ".rpm",
        ".m3u8", ".mpd",
    };

    public void Attach()
    {
        if (_source is not null) return;

        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero)
            throw new InvalidOperationException("Jendela belum punya handle; panggil Attach setelah SourceInitialized.");

        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        AddClipboardFormatListener(helper.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && Enabled)
        {
            // Clipboard harus dibaca di UI thread; pesan ini memang datang di UI thread.
            TryReadClipboard();
        }

        return IntPtr.Zero;
    }

    private void TryReadClipboard()
    {
        string? text = null;

        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (COMException)
        {
            // Clipboard sedang dikunci proses lain; abaikan, event berikutnya akan datang.
            return;
        }
        catch (ExternalException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        text = text.Trim();
        if (text.Length > 2048) return;

        if (!LooksLikeDownloadUrl(text)) return;
        if (string.Equals(text, _lastUrl, StringComparison.Ordinal)) return;

        _lastUrl = text;
        UrlDetected?.Invoke(text);
    }

    /// <summary>
    /// Heuristik: URL http/https dengan ekstensi file yang dikenal, atau
    /// parameter penyimpanan seperti <c>.zip?</c>. Situs video HLS juga dianggap.
    /// </summary>
    public static bool LooksLikeDownloadUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        string path = uri.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path[(slash + 1)..] : path;
        if (name.Length == 0)
        {
            // URL tanpa nama berkas: kemungkinan halaman biasa, bukan unduhan.
            return false;
        }

        string lower = name.ToLowerInvariant();
        foreach (string ext in DownloadExtensions)
        {
            if (lower.EndsWith(ext, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        var helper = new WindowInteropHelper(_window);
        if (helper.Handle != IntPtr.Zero)
        {
            try { RemoveClipboardFormatListener(helper.Handle); } catch { /* jendela mungkin sudah ditutup */ }
        }
    }
}
