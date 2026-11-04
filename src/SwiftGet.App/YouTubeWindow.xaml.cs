using System.Windows;
using SwiftGet.Core.YouTube;

namespace SwiftGet.App;

/// <summary>
/// Daftar kualitas video YouTube hasil probe yt-dlp. Pengguna memilih satu;
/// audio terbaik diunduh dan digabung otomatis oleh view model.
/// </summary>
public partial class YouTubeWindow : Window
{
    /// <summary>Format yang dipilih pengguna; null berarti batal.</summary>
    public YouTubeFormat? Terpilih { get; private set; }

    public YouTubeWindow(Core.YouTube.YouTubeMeta meta)
    {
        InitializeComponent();
        JudulText.Text = string.IsNullOrWhiteSpace(meta.Title) ? "Video tanpa judul" : meta.Title;
        DaftarFormat.ItemsSource = meta.Video;
        DaftarFormat.SelectedIndex = 0;
    }

    private void Unduh_Click(object sender, RoutedEventArgs e)
    {
        Terpilih = DaftarFormat.SelectedItem as YouTubeFormat;
        if (Terpilih is null)
        {
            MessageBox.Show(this, "Pilih salah satu kualitas terlebih dahulu.",
                "SwiftGet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Batal_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
