using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Memantau perubahan preferensi animasi Windows secara langsung.
/// <para>
/// <see cref="Motion"/> membaca nilai awal satu kali. Kelas ini melengkapi:
/// menyiarkan perubahan saat pengguna mengganti pengaturan "kurangi gerakan"
/// tanpa harus me-restart aplikasi.
/// </para>
/// </summary>
public sealed class MotionPreferenceWatcher : IDisposable
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private readonly HwndSource _source;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    /// <summary>Dipicu saat preferensi gerakan Windows berganti.</summary>
    public event Action<bool>? ReduceMotionChanged;

    public MotionPreferenceWatcher(Window window)
    {
        _dispatcher = window.Dispatcher;

        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => Attach(window);
            _source = null!;
            return;
        }

        _source = HwndSource.FromHwnd(helper.Handle) ?? throw new InvalidOperationException("HwndSource tidak tersedia.");
        _source.AddHook(WndProc);
    }

    private void Attach(Window window)
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        var src = HwndSource.FromHwnd(helper.Handle);
        if (src is null) return;
        src.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE)
        {
            // Tidak ada penanda khusus untuk animasi, jadi periksa ulang nilai.
            bool reduce = ReadDisableAnimations();
            if (reduce != Motion.ReduceMotion)
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    Motion.UpdateReduceMotion(reduce);
                    ReduceMotionChanged?.Invoke(reduce);
                }));
            }
        }

        return IntPtr.Zero;
    }

    private static bool ReadDisableAnimations()
    {
        try
        {
            bool enabled = true;
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0))
            {
                return !enabled;
            }
        }
        catch (Exception)
        {
            // Gagal membaca preferensi bukan alasan untuk gagal.
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint update);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _source?.RemoveHook(WndProc); } catch { }
    }
}

