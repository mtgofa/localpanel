using System.Windows;
using LocalPanel.Core;

namespace LocalPanel;

public partial class App : Application
{
    public static ServiceManager Manager { get; private set; } = null!;

    public static string Version
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v is null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second launch signals the first one to show itself, then quits.
        _instanceMutex = new Mutex(initiallyOwned: true, "LocalPanel_SingleInstance", out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "LocalPanel_ShowSignal");
        if (!isFirst)
        {
            // Tell the running instance to show itself, then leave immediately —
            // before WPF gets a chance to create our window.
            _showSignal.Set();
            Environment.Exit(0);
        }

        // Wake up and show the window whenever another launch signals us.
        var signal = _showSignal;
        Task.Run(() =>
        {
            while (signal.WaitOne())
                Dispatcher.Invoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        });

        base.OnStartup(e);
        Manager = new ServiceManager();
        CheckPrerequisites();

        // The window is created here instead of through StartupUri so "start minimized"
        // can skip Show() altogether. It is a WindowStyle=None + WindowChrome window:
        // showing it and calling Hide() during Loaded left an unpainted black frame on
        // screen until it was reopened from the tray.
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // nothing is open while we sit in the tray
        var window = new MainWindow();
        MainWindow = window;
        if (!Manager.Config.StartMinimizedToTray) window.Show();

        // Keep junctions/shims/PATH in sync with the installed versions.
        _ = Task.Run(() => { try { ShimManager.Apply(Manager.Config); } catch { } });

        // If the folder was moved, refresh the launch-at-login path too.
        _ = Task.Run(() => { try { if (StartupManager.IsEnabled) StartupManager.SetEnabled(true); } catch { } });

        // Park-import sites and keep the hosts file + nginx config in sync on every
        // launch, THEN auto-start services: nginx must not generate its config while
        // ImportParkedSites is still mutating Config.Sites, and the sites list the
        // window built at construction time is refreshed once the import is done.
        _ = Task.Run(async () =>
        {
            try { await Manager.ApplySitesAsync(); } catch { }
            _ = Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.RefreshSitesListPublic());
            if (!Manager.Config.AutoStartServicesOnLaunch) return;
            await Manager.StartAutoStartServicesAsync((name, ex) =>
                Dispatcher.Invoke(() =>
                    MessageBox.Show($"{name}: {ex.Message}", "LocalPanel — auto start",
                        MessageBoxButton.OK, MessageBoxImage.Warning)));
        });

        // New Firefox profiles get the Windows-store trust flag automatically.
        _ = Task.Run(() => { try { SslManager.EnableFirefoxTrust(); } catch { } });

        // mkcert and Composer keep themselves fresh silently.
        _ = Task.Run(async () => { try { await Manager.AutoUpdateMkcertAsync(); } catch { } });
        _ = Task.Run(async () => { try { await Manager.AutoUpdateComposerAsync(); } catch { } });
    }

    /// <summary>Instant check (two File.Exists calls) — never slows startup, and
    /// service-start failures carry the same hint via Prereqs.Hint anyway.</summary>
    private static void CheckPrerequisites()
    {
        try
        {
            if (!Prereqs.VcRuntimeMissing) return;
        }
        catch { return; }

        var res = MessageBox.Show(
            "LocalPanel needs the Microsoft Visual C++ Runtime (2015-2022), which is not installed on this PC.\n\n" +
            "PHP, MySQL and Nginx cannot run without it.\n\n" +
            "Open the official Microsoft download now?",
            "LocalPanel — missing requirement",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://aka.ms/vs/17/release/vc_redist.x64.exe") { UseShellExecute = true });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Windows logoff/shutdown raises SessionEnding and then skips Window.Closing,
    /// so ExitApp → StopAllAsync would never run and mysqld/mongod/redis would be
    /// terminated mid-flush. Stop them gracefully here (bounded to 10 s) and tell
    /// Windows why the shutdown is briefly held.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel) return;
        var hwnd = MainWindow is null
            ? IntPtr.Zero
            : new System.Windows.Interop.WindowInteropHelper(MainWindow).Handle;
        if (hwnd != IntPtr.Zero)
        {
            try { ShutdownBlockReasonCreate(hwnd, "Stopping local services (MySQL, nginx…)"); } catch { }
        }
        // Must run off the Dispatcher thread: StopAllAsync awaits without
        // ConfigureAwait(false), so a plain .Wait() here would deadlock.
        try { Task.Run(() => Manager.StopAllAsync()).Wait(TimeSpan.FromSeconds(10)); } catch { }
        if (hwnd != IntPtr.Zero)
        {
            try { ShutdownBlockReasonDestroy(hwnd); } catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
