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

        // Keep junctions/shims/PATH in sync with the installed versions.
        _ = Task.Run(() => { try { ShimManager.Apply(Manager.Config); } catch { } });

        // Park-import sites and keep the hosts file + nginx config in sync on every launch.
        _ = Task.Run(async () => { try { await Manager.ApplySitesAsync(); } catch { } });

        // New Firefox profiles get the Windows-store trust flag automatically.
        _ = Task.Run(() => { try { SslManager.EnableFirefoxTrust(); } catch { } });

        if (Manager.Config.AutoStartServicesOnLaunch)
        {
            _ = Manager.StartAutoStartServicesAsync((name, ex) =>
                Dispatcher.Invoke(() =>
                    MessageBox.Show($"{name}: {ex.Message}", "LocalPanel — auto start",
                        MessageBoxButton.OK, MessageBoxImage.Warning)));
        }
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
}
