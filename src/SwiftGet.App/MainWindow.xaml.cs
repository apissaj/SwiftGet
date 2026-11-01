using System.Windows;
using System.Windows.Forms;
using SwiftGet.App.Infrastructure;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App;

/// <summary>
/// Jendela utama: daftar unduhan, input URL, tray icon, dan pemantau clipboard.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly ClipboardWatcher _clipboard;
    private NotifyIcon? _tray;
    private bool _closeRequested;
    private bool _balloonShown;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        _viewModel.DownloadCompleted += OnDownloadCompleted;

        _clipboard = new ClipboardWatcher(this) { Enabled = settings.WatchClipboard };
        _clipboard.UrlDetected += OnClipboardUrl;

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _clipboard.Attach();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus("Pemantau clipboard tidak aktif: " + ex.Message);
        }

        SetupTray();
    }

    private void SetupTray()
    {
        try
        {
            _tray = new NotifyIcon
            {
                Visible = true,
                Text = "SwiftGet",
                Icon = CreateTrayIcon(),
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Buka SwiftGet", null, (_, _) => ShowFromTray());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Jeda semua", null, (_, _) => _viewModel.PauseAllCommand.Execute(null));
            menu.Items.Add("Lanjutkan semua", null, (_, _) => _viewModel.ResumeAllCommand.Execute(null));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Keluar", null, (_, _) => ExitApplication());

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus("Tray tidak tersedia: " + ex.Message);
        }
    }

    /// <summary>
    /// Ikon tray dibuat dari geometri sederhana, bukan berkas .ico, agar aplikasi
    /// tetap satu exe tanpa aset eksternal.
    /// </summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x2D, 0x6C, 0xDF));
            g.FillEllipse(background, 0, 0, 31, 31);

            using var arrowPen = new System.Drawing.Pen(System.Drawing.Color.White, 3.2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            // Panah ke bawah + dasar, simbol unduhan.
            g.DrawLine(arrowPen, 16, 8, 16, 20);
            g.DrawLine(arrowPen, 10, 15, 16, 21);
            g.DrawLine(arrowPen, 22, 15, 16, 21);
            g.DrawLine(arrowPen, 9, 25, 23, 25);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private void OnClipboardUrl(string url)
    {
        _viewModel.OnClipboardUrl(url);

        if (_settings.StartDownloadsAutomatically)
        {
            _viewModel.AddCommand.Execute(null);
        }
        else
        {
            ShowFromTray();
        }
    }

    private void OnDownloadCompleted(DownloadItemViewModel item)
    {
        if (_tray is null || _balloonShown) return;

        // Batasi notifikasi agar tidak mengganggu saat banyak unduhan selesai.
        _balloonShown = true;
        try
        {
            _tray.BalloonTipTitle = "Unduhan selesai";
            _tray.BalloonTipText = item.FileName;
            _tray.ShowBalloonTip(3000);
        }
        catch (Exception) { /* notifikasi gagal bukan masalah */ }

        _ = System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ => _balloonShown = false,
            System.Threading.Tasks.TaskScheduler.Default);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeRequested)
        {
            CleanupTray();
            return;
        }

        if (_settings.MinimizeToTray && _tray is not null)
        {
            // Sembunyikan ke tray, jangan matikan unduhan yang sedang berjalan.
            e.Cancel = true;
            Hide();
            return;
        }

        CleanupTray();
    }

    private void CleanupTray()
    {
        if (_tray is null) return;
        try
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch (Exception) { /* abaikan */ }
        _tray = null;
    }

    private void ExitApplication()
    {
        _closeRequested = true;
        CleanupTray();
        _clipboard.Dispose();
        Application.Current.Shutdown();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings.Save();
        _clipboard.Enabled = _settings.WatchClipboard;
        _viewModel.SetStatus(
            "Pengaturan disimpan. Jumlah koneksi baru berlaku untuk unduhan berikutnya.");
    }
}
