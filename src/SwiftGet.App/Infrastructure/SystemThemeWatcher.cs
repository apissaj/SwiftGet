using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Memantau perubahan tema Windows secara langsung.
/// <para>
/// <see cref="SystemThemeWatcher"/> membaca nilai awal dan menyiarkan
/// perubahan saat pengguna mengganti tema Windows ketika aplikasi sudah
/// berjalan. Tanpa ini, pengguna yang memakai mode "ikuti sistem" harus
/// me-restart aplikasi supaya tema ikut berganti.
/// </para>
/// </summary>
public sealed class SystemThemeWatcher : IDisposable
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private HwndSource? _source;
    private readonly SynchronizationContext? _syncContext;
    private bool _disposed;

    /// <summary>Dipicu saat tema Windows berganti (true = gelap).</summary>
    public event Action<bool>? ThemeChanged;

    public SystemThemeWatcher(Window window)
    {
        _syncContext = SynchronizationContext.Current;

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            Attach(helper.Handle);
        }
        else
        {
            // Jendela belum punya handle: pasang setelah siap. Satu jalur saja
            // supaya tidak ada dua sumber hook yang berbeda.
            window.SourceInitialized += OnSourceInitialized;
            _window = window;
        }
    }

    private Window? _window;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_window is null) return;
        _window.SourceInitialized -= OnSourceInitialized;

        IntPtr handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero) Attach(handle);
        _window = null;
    }

    private void Attach(IntPtr handle)
    {
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_SETTINGCHANGE) return IntPtr.Zero;

        // Hanya tanggapi perubahan warna, bukan setiap WM_SETTINGCHANGE.
        string? section = lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);
        if (!string.Equals(section, ImmersiveColorSet, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        bool dark = IsSystemDark();

        // Callback hook berjalan di thread UI, tapi Post menjaga urutan dengan
        // perubahan tema lain yang mungkin dipicu dari luar hook.
        _syncContext?.Post(_ => ThemeChanged?.Invoke(dark), null);

        return IntPtr.Zero;
    }

    /// <summary>Baca preferensi tema aplikasi Windows saat ini.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            object? value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);

            return value is int light && light == 0;
        }
        catch (Exception)
        {
            // Preferensi tidak terbaca bukan alasan untuk gagal start.
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_window is not null)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            _window = null;
        }

        try { _source?.RemoveHook(WndProc); } catch { }
        _source = null;
    }
}
