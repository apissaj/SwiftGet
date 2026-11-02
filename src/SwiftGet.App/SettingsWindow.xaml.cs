using System.Windows;
using SwiftGet.App.Infrastructure;

namespace SwiftGet.App;

/// <summary>
/// Dialog pengaturan. Mengubah nilai di sini tidak langsung berlaku; perubahan
/// diterapkan ke <see cref="AppSettings"/> saat tombol Simpan ditekan.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>
    /// False selama XAML masih dimuat. Event <c>ValueChanged</c> pada Slider
    /// terpicu saat properti diset dari markup, yaitu sebelum kontrol lain ada.
    /// </summary>
    private bool _ready;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;

        FolderBox.Text = settings.DownloadDirectory;

        ConcurrentSlider.Value = settings.MaxConcurrentDownloads;
        SegmentsSlider.Value = settings.MaxConnectionsPerDownload;
        PerHostSlider.Value = settings.MaxConnectionsPerHost;

        FfmpegBox.Text = settings.FfmpegPath;

        WatchClipboardBox.IsChecked = settings.WatchClipboard;
        AutoStartBox.IsChecked = settings.StartDownloadsAutomatically;
        MinimizeToTrayBox.IsChecked = settings.MinimizeToTray;

        LocalEndpointBox.IsChecked = settings.EnableLocalEndpoint;
        PortBox.Text = settings.LocalEndpointPort.ToString();
        TokenBox.Text = settings.LocalEndpointToken;

        _ready = true;
        UpdateLabels();
    }

    private void Concurrent_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void Segments_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void PerHost_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void UpdateLabels()
    {
        if (!_ready) return;
        if (ConcurrentLabel is null || SegmentsLabel is null || PerHostLabel is null) return;

        ConcurrentLabel.Text = $"{(int)ConcurrentSlider.Value} unduhan sekaligus";
        SegmentsLabel.Text = $"{(int)SegmentsSlider.Value} bagian per berkas";
        PerHostLabel.Text = $"{(int)PerHostSlider.Value} koneksi maksimum ke server yang sama";
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pilih folder unduhan",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : "",
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.DownloadDirectory = FolderBox.Text;
        _settings.MaxConcurrentDownloads = (int)ConcurrentSlider.Value;
        _settings.MaxConnectionsPerDownload = (int)SegmentsSlider.Value;
        _settings.MaxConnectionsPerHost = (int)PerHostSlider.Value;

        _settings.FfmpegPath = FfmpegBox.Text.Trim();

        _settings.WatchClipboard = WatchClipboardBox.IsChecked == true;
        _settings.StartDownloadsAutomatically = AutoStartBox.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;

        _settings.EnableLocalEndpoint = LocalEndpointBox.IsChecked == true;
        // Port yang tidak bisa diurai kembali ke nilai lama, bukan 0.
        if (int.TryParse(PortBox.Text.Trim(), out int port))
            _settings.LocalEndpointPort = port;

        _settings.Normalize();

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
