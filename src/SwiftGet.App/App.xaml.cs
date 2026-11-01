using System.Windows;
using SwiftGet.App.Infrastructure;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App;

/// <summary>
/// Titik masuk aplikasi. Menjaga satu instance, memuat pengaturan dan tema,
/// lalu menampilkan jendela utama.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\SwiftGet.SingleInstance.v1";
    private const string ShowSignalName = @"Local\SwiftGet.ShowSignal.v1";

    private Mutex? _instanceMutex;
    private MainViewModel? _viewModel;
    private EventWaitHandle? _showSignal;
    private Thread? _signalThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "Terjadi kesalahan tak terduga:\n\n" + args.Exception.Message,
                "SwiftGet", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Motion.Initialize();

        AppSettings settings = AppSettings.Load();
        bool dark = settings.DarkTheme ?? IsSystemDark();
        ThemeManager.Apply(dark);

        _viewModel = new MainViewModel(settings, Dispatcher) { IsDark = dark };

        var window = new MainWindow(_viewModel, settings);
        MainWindow = window;
        window.Show();

        StartShowSignalListener();

        _ = _viewModel.InitializeAsync();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out EventWaitHandle? signal))
            {
                signal.Set();
                signal.Dispose();
            }
        }
        catch (Exception)
        {
            // Memberi sinyal gagal bukan alasan untuk crash.
        }
    }

    /// <summary>
    /// Baca preferensi tema Windows saat belum ada pilihan tersimpan.
    /// Aplikasi bertema gelap secara eksplisit hanya karena alasan kuat
    /// (R-21); SwiftGet mengikuti sistem, pengguna tetap bisa menggantinya.
    /// </summary>
    private static bool IsSystemDark()
    {
        try
        {
            object? value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", null);

            return value is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void StartShowSignalListener()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);

        _signalThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showSignal.WaitOne()) return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow is MainWindow window) window.ShowFromTray();
                }));
            }
        })
        {
            IsBackground = true,
            Name = "SwiftGet.ShowSignal",
        };

        _signalThread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.Dispose();
            _showSignal?.Dispose();
        }
        catch (Exception)
        {
            // Keluar secepat mungkin.
        }

        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}

