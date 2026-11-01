using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Token gerak bersama sesuai `DESIGN.md` bagian 10.
/// <para>
/// Semua durasi berasal dari sini supaya gerak tetap konsisten dan bisa
/// dimatikan di satu tempat ketika pengguna memilih "kurangi gerakan".
/// </para>
/// </summary>
public static class Motion
{
    /// <summary>Hover dan fokus: cukup cepat untuk terasa langsung.</summary>
    public static readonly TimeSpan Hover = TimeSpan.FromMilliseconds(120);

    /// <summary>Perubahan bentuk baris (mode penuh ke ringkas dan sebaliknya).</summary>
    public static readonly TimeSpan Layout = TimeSpan.FromMilliseconds(200);

    /// <summary>Panel periksa masuk dan keluar.</summary>
    public static readonly TimeSpan Panel = TimeSpan.FromMilliseconds(180);

    /// <summary>Segmen menyala saat mencapai 100 persen.</summary>
    public static readonly TimeSpan Segment = TimeSpan.FromMilliseconds(260);

    /// <summary>
    /// True bila pengguna meminta gerakan dikurangi lewat setelan Windows.
    /// Dibaca sekali saat aplikasi mulai; perubahan saat berjalan belum
    /// ditangani (lihat DESIGN.md bagian 16).
    /// </summary>
    public static bool ReduceMotion { get; private set; }

    /// <summary>
    /// Durasi efektif: nol ketika gerakan dikurangi.
    /// </summary>
    public static TimeSpan Effective(TimeSpan desired) => ReduceMotion ? TimeSpan.Zero : desired;

    /// <summary>
    /// Baca preferensi animasi sistem. Dipanggil sekali di startup.
    /// </summary>
    public static void Initialize()
    {
        ReduceMotion = ReadDisableAnimations();
    }

    /// <summary>Kurva standar: cepat di awal, tenang di akhir.</summary>
    public static IEasingFunction EaseOut { get; } = CreateEaseOut();

    private static IEasingFunction CreateEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static bool ReadDisableAnimations()
    {
        try
        {
            // SPI_GETCLIENTAREAANIMATION (0x1042): false berarti animasi dimatikan.
            const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
            bool enabled = true;
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0))
            {
                return !enabled;
            }
        }
        catch (Exception)
        {
            // Gagal membaca preferensi bukan alasan untuk gagal jalan.
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint update);
}
