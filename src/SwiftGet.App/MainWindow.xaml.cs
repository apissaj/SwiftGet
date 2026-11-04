using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwiftGet.App.Infrastructure;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App;

/// <summary>
/// Jendela utama: daftar unduhan, panel periksa, tray, dan pemantau clipboard.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly ClipboardWatcher _clipboard;
    private NotifyIcon? _tray;
    private bool _closeRequested;
    private bool _balloonShown;

    /// <summary>
    /// Ambang lebar untuk panel periksa menjadi lapisan. Di bawah nilai ini
    /// daftar tidak cukup lebar untuk berbagi ruang dengan panel 304 px.
    /// </summary>
    private const double OverlayBreakpoint = 1000;

    private SystemThemeWatcher? _themeWatcher;
    private MotionPreferenceWatcher? _motionWatcher;
    private LocalEndpointServer? _localEndpoint;
    private FocusTrap? _focusTrap;
    private bool _inspectorOverlay;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        _viewModel.DownloadCompleted += OnDownloadCompleted;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.FocusUrlRequested += (_, _) =>
        {
            UrlField.Focus();
            Keyboard.Focus(UrlField);
        };

        _clipboard = new ClipboardWatcher(this) { Enabled = settings.WatchClipboard };
        _clipboard.UrlDetected += OnClipboardUrl;

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        SizeChanged += OnSizeChanged;

        // Fokus awal: kotak URL adalah focal point layar ini.
        Loaded += (_, _) => UrlField.Focus();
    }

    /// <summary>
    /// Palet dibaca ulang setiap kali tema berganti. Ini satu-satunya tempat
    /// <see cref="ThemeManager"/> dipanggil saat runtime, supaya tidak ada
    /// sumber kebenaran kedua.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDark))
        {
            ThemeManager.Apply(_viewModel.IsDark);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsInspectorOpen))
        {
            // Overlay sempit butuh kurungan fokus tepat saat panel terbuka.
            UpdateScrim();

            if (!_inspectorOverlay) return;
            if (_viewModel.IsInspectorOpen) _focusTrap?.Arm();
            else _focusTrap?.Disarm();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.IsInspectorOpen)
        {
            _viewModel.CloseInspectorCommand.Execute(null);
            e.Handled = true;
        }
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
        SetupWatchers();
        SetupLocalEndpoint();
        UpdateInspectorLayout();
    }

    /// <summary>
    /// Endpoint bookmarklet hanya menyala bila pengguna menyalakannya di
    /// pengaturan. Kegagalan bind (port dipakai aplikasi lain) bukan alasan
    /// gagal start; cukup dilaporkan di baris status.
    /// </summary>
    private void SetupLocalEndpoint()
    {
        if (!_settings.EnableLocalEndpoint) return;

        try
        {
            // Token dibuat sekali saat endpoint pertama disalakan; ekstensi
            // dan bookmarklet memakai nilai yang sama sampai dibuat ulang.
            if (string.IsNullOrWhiteSpace(_settings.LocalEndpointToken))
            {
                _settings.LocalEndpointToken = Guid.NewGuid().ToString("N")[..20];
                _settings.Save();
            }

            _localEndpoint = new LocalEndpointServer(_settings.LocalEndpointPort,
                _settings.LocalEndpointToken, AddFromBrowserAsync);
            _localEndpoint.Start();
            _viewModel.SetStatus("Menerima unduhan dari browser di " + _localEndpoint.BaseUrl);
        }
        catch (Exception ex)
        {
            _localEndpoint = null;
            _viewModel.SetStatus("Endpoint browser tidak aktif: " + ex.Message);
        }
    }

    private async Task<LocalEndpointServer.AddResult> AddFromBrowserAsync(LocalEndpointServer.AddRequest request)
    {
        // Endpoint berjalan di thread pool; operasi daftar harus di thread UI.
        Task<(bool Ok, string Message)> op = await Dispatcher.InvokeAsync(
            () => _viewModel.AddFromLocalEndpointAsync(request)).Task.ConfigureAwait(false);
        var (ok, message) = await op.ConfigureAwait(false);
        return new LocalEndpointServer.AddResult(ok, message);
    }

    /// <summary>Sesuaikan endpoint dengan pengaturan terbaru (saklar dan port).</summary>
    private void ApplyLocalEndpointSetting()
    {
        if (!_settings.EnableLocalEndpoint)
        {
            _localEndpoint?.Dispose();
            _localEndpoint = null;
            return;
        }

        // Salakan ulang tanpa peduli apa yang berubah: listener lama ditutup
        // lebih dulu, jadi tidak ada dua listener merebut port yang sama.
        _localEndpoint?.Dispose();
        _localEndpoint = null;
        SetupLocalEndpoint();
    }

    /// <summary>
    /// Watcher tema dan gerakan dipasang setelah jendela punya handle, karena
    /// keduanya butuh WM_SETTINGCHANGE dari jendela ini.
    /// </summary>
    private void SetupWatchers()
    {
        try
        {
            _themeWatcher = new SystemThemeWatcher(this);
            _themeWatcher.ThemeChanged += OnSystemThemeChanged;
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus("Pemantau tema tidak aktif: " + ex.Message);
        }

        try
        {
            _motionWatcher = new MotionPreferenceWatcher(this);
            _motionWatcher.ReduceMotionChanged += OnReduceMotionChanged;
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus("Pemantau gerakan tidak aktif: " + ex.Message);
        }
    }

    /// <summary>
    /// Tema sistem hanya berlaku saat pengguna memilih "ikuti sistem".
    /// Pilihan manual selalu menang, jadi tema yang dipilih tidak dilawan
    /// oleh perubahan latar belakang.
    /// </summary>
    private void OnSystemThemeChanged(bool dark)
    {
        if (_viewModel.DarkThemeOverride is not null) return;
        _viewModel.ApplySystemTheme(dark);
    }

    private void OnReduceMotionChanged(bool reduce)
    {
        // Motion membaca flag ini lewat Effective(); beri tahu tampilan supaya
        // grafik berhenti menganimasikan tanpa perlu restart.
        _viewModel.SetStatus(reduce
            ? "Animasi dimatikan mengikuti pengaturan Windows"
            : "Animasi aktif kembali");
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
            menu.Items.Add("Riwayat", null, (_, _) =>
            {
                ShowFromTray();
                OpenHistory();
            });
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
    /// Ikon tray dibuat dari geometri, bukan berkas .ico, supaya aplikasi
    /// tetap satu berkas tanpa aset eksternal.
    /// </summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x0F, 0x6B, 0x5C));
            g.FillEllipse(background, 0, 0, 31, 31);

            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawLine(pen, 16, 8, 16, 20);
            g.DrawLine(pen, 10, 15, 16, 21);
            g.DrawLine(pen, 22, 15, 16, 21);
            g.DrawLine(pen, 9, 25, 23, 25);
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

        // Batasi notifikasi agar tidak mengganggu saat banyak unduhan selesai bersamaan.
        _balloonShown = true;
        try
        {
            _tray.BalloonTipTitle = "Unduhan selesai";
            _tray.BalloonTipText = item.FileName;
            _tray.ShowBalloonTip(3000);
        }
        catch (Exception)
        {
            // Notifikasi gagal bukan masalah fungsional.
        }

        _ = System.Threading.Tasks.Task.Delay(5000)
            .ContinueWith(_ => _balloonShown = false, System.Threading.Tasks.TaskScheduler.Default);
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
            // Sembunyikan ke tray; unduhan yang berjalan tetap lanjut.
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
        catch (Exception)
        {
            // Tray mungkin sudah dibuang.
        }
        _tray = null;
    }

    private void ExitApplication()
    {
        _closeRequested = true;
        CleanupTray();
        _clipboard.Dispose();
        _localEndpoint?.Dispose();
        _localEndpoint = null;

        _themeWatcher?.Dispose();
        _themeWatcher = null;
        _motionWatcher?.Dispose();
        _motionWatcher = null;
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Tombol urut bergilir: Terbaru, Status, Ukuran, lalu kembali. Satu
    /// kontrol untuk tiga pilihan lebih tenang daripada tiga tombol yang
    /// selalu terlihat, dan label tombol menunjukkan pilihan aktif.
    /// </summary>
    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadsViewModel downloads = _viewModel.Downloads;
        downloads.Sort = downloads.Sort switch
        {
            DownloadSort.Newest => DownloadSort.Status,
            DownloadSort.Status => DownloadSort.Size,
            _ => DownloadSort.Newest,
        };
    }

    /// <summary>Klik di luar panel menutup lapisan, seperti dialog.</summary>
    private void InspectorScrim_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.CloseInspectorCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateInspectorLayout();

    /// <summary>
    /// Di jendela lebar panel menempel di kolom kanan. Di jendela sempit ia
    /// menjadi lapisan di atas daftar, dengan scrim dan kurungan fokus.
    /// </summary>
    private void UpdateInspectorLayout()
    {
        bool overlay = ActualWidth > 0 && ActualWidth < OverlayBreakpoint;

        if (overlay == _inspectorOverlay)
        {
            // Lebar berubah tapi status tetap: cukup pastikan posisi benar.
            ApplyInspectorColumns(overlay);
            return;
        }

        _inspectorOverlay = overlay;
        ApplyInspectorColumns(overlay);

        if (overlay)
        {
            _focusTrap ??= new FocusTrap(this, InspectorPanel,
                () => _viewModel.CloseInspectorCommand.Execute(null));

            // Di mode lapisan, panel harus setinggi isi jendela.
            InspectorPanel.Margin = new Thickness(0, 0, 0, 0);
            InspectorPanel.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            InspectorPanel.MaxHeight = double.PositiveInfinity;

            if (_viewModel.IsInspectorOpen) ArmInlineTrap();
        }
        else
        {
            _focusTrap?.Disarm();
            InspectorPanel.Margin = new Thickness(0, 16, 20, 12);
            InspectorPanel.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        }

        UpdateScrim();
    }

    private void ApplyInspectorColumns(bool overlay)
    {
        if (overlay)
        {
            Grid.SetColumn(InspectorPanel, 0);
            Grid.SetColumnSpan(InspectorPanel, 2);
            InspectorPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            System.Windows.Controls.Panel.SetZIndex(InspectorPanel, 10);
            InspectorPanel.Width = 304;
        }
        else
        {
            Grid.SetColumn(InspectorPanel, 1);
            Grid.SetColumnSpan(InspectorPanel, 1);
            InspectorPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            System.Windows.Controls.Panel.SetZIndex(InspectorPanel, 0);
            InspectorPanel.Width = 304;
        }
    }

    private void ArmInlineTrap()
    {
        if (_focusTrap is null) return;
        _focusTrap.Arm();
    }

    private void UpdateScrim()
    {
        bool show = _inspectorOverlay && _viewModel.IsInspectorOpen;
        InspectorScrim.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings.Save();
        _clipboard.Enabled = _settings.WatchClipboard;
        _viewModel.ReloadExclusions();
        _viewModel.TerapkanBatasKecepatan();
        ApplyLocalEndpointSetting();
        _viewModel.SetStatus("Pengaturan disimpan, berlaku untuk unduhan berikutnya");
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e) => OpenHistory();

    private void Jadwal_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is null) return;

        var dialog = new JadwalWindow(_viewModel.SelectedItem.Task.ScheduledStartUtc) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _viewModel.AturJadwal(_viewModel.SelectedItem.Id, dialog.Hasil, dialog.MatikanPc);
    }

    private void OpenHistory()
    {
        var dialog = new HistoryWindow(_viewModel.History) { Owner = this };
        dialog.ShowDialog();
    }
}



