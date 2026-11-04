using System.Globalization;
using System.Windows;
using SwiftGet.App.Infrastructure;

namespace SwiftGet.App;

/// <summary>
/// Dialog "Unduh nanti": satu waktu mulai otomatis per unduhan, plus opsi
/// mematikan komputer saat antrian tuntas. Penguraian jam gagal ditandai
/// di tempat, bukan melempar — pengguna tinggal memperbaiki isian.
/// </summary>
public partial class JadwalWindow : Window
{
    private readonly DateTimeOffset? _jadwalAwal;

    /// <summary>Jadwal yang dipilih (UTC); null berarti menghapus jadwal.</summary>
    public DateTimeOffset? Hasil { get; private set; }

    public bool MatikanPc { get; private set; }

    public JadwalWindow(DateTimeOffset? jadwalAwal)
    {
        InitializeComponent();
        _jadwalAwal = jadwalAwal;

        if (jadwalAwal is DateTimeOffset s)
        {
            JamBox.Text = s.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            MatikanPcBox.IsChecked = true; // jadwal lama biasanya datang dengan opsi ini
        }
        else
        {
            HapusJadwalButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Simpan_Click(object sender, RoutedEventArgs e)
    {
        if (!TimeSpan.TryParseExact(JamBox.Text.Trim(), "hh\\:mm",
                CultureInfo.InvariantCulture, out TimeSpan jam))
        {
            MessageBox.Show(this, "Isi jam dengan format HH:mm, misal 02:00.",
                "SwiftGet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DateTimeOffset jadwal = DateTimeOffset.Now.Date + jam;
        if (jadwal <= DateTimeOffset.Now.AddMinutes(-1))
            jadwal = jadwal.AddDays(1); // sudah lewat: besok pada jam yang sama

        Hasil = jadwal.ToUniversalTime();
        MatikanPc = MatikanPcBox.IsChecked == true;
        DialogResult = true;
    }

    private void HapusJadwal_Click(object sender, RoutedEventArgs e)
    {
        Hasil = null;
        MatikanPc = false;
        DialogResult = true;
    }

    private void Batal_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
