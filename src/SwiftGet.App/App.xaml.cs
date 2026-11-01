using System.Windows;
using SwiftGet.App.Infrastructure;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App;

/// <summary>
/// Titik masuk aplikasi. Menjaga satu instance, memuat pengaturan, lalu
/// menampilkan jendela utama.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\SwiftGet.SingleInstance.v1";

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
            // Instance lain sudah berjalan: minta jendelanya muncul lalu keluar.
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\SwiftGet.ShowSignal.v1", out EventWaitHandle? signal))
                {
                    signal.Set();
                    signal.Dispose();
                }
            }
            catch (Exception) { /* gagal memberi sinyal bukan alasan untuk crash */ }

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

        var settings = AppSettings.Load();

        ThemeManager.Apply(dark: false);
        ApplyAccent();

        _viewModel = new MainViewModel(settings, Dispatcher);

        var window = new MainWindow(_viewModel, settings);
        MainWindow = window;
        window.Show();

        StartShowSignalListener();

        _ = _viewModel.InitializeAsync();
    }

    /// <summary>Dengarkan permintaan dari instance kedua untuk menampilkan jendela.</summary>
    private void StartShowSignalListener()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SwiftGet.ShowSignal.v1");

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
                    if (MainWindow is MainWindow w)
                    {
                        w.ShowFromTray();
                    }
                }));
            }
        })
        {
            IsBackground = true,
            Name = "SwiftGet.ShowSignal",
        };

        _signalThread.Start();
    }

    private static void ApplyAccent()
    {
        // Aksen default dari tema; dibiarkan di satu tempat agar mudah diganti.
        ThemeManager.Apply(dark: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.Dispose();
            _showSignal?.Dispose();
        }
        catch (Exception) { /* keluar secepat mungkin */ }

        try { _instanceMutex?.ReleaseMutex(); } catch { /* mungkin sudah dilepas */ }
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}


