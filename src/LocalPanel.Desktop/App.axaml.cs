using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LocalPanel.Core;

namespace LocalPanel.Desktop;

public partial class App : Application
{
    /// <summary>One manager for the whole process — the window and the tray menu drive
    /// the same services, so a second instance would fight the first over ports and config.</summary>
    public static ServiceManager Manager { get; private set; } = null!;

    public static string Version
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v is null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private MainWindow? _window;
    private FileSystemWatcher? _showWatcher;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Manager = new ServiceManager();

            // Closing the window only hides it to the tray, so the app must not quit
            // when the last window disappears — Exit lives in the tray menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Keep shims (php/node/composer plus the localpanel + lpanel CLI aliases) in sync
            // with what is installed — the same thing the Windows build does on startup.
            _ = Task.Run(() =>
            {
                try { ShimManager.Apply(Manager.Config); ShimManager.EnsureUserPath(); }
                catch { }
            });

            _window = new MainWindow();
            desktop.MainWindow = _window;
            if (!Manager.Config.StartMinimizedToTray) _window.Show();

            // Launching localpanel-gui again surfaces this window instead of starting a
            // second copy — so the window is always reachable even with no tray host.
            _showWatcher = SingleInstanceLock.WatchShowRequests(Program.InstanceName,
                () => Dispatcher.UIThread.Post(() => _window?.ShowFromTray()));
            desktop.Exit += (_, _) => _showWatcher?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ---------------- tray menu ----------------
    private void TrayOpen_Click(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => _window?.ShowFromTray());

    private void TrayStartAll_Click(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(async void () =>
        {
            try { await Manager.StartAutoStartServicesAsync(); }
            catch { /* surfaced in the window's status bar on the next refresh */ }
            if (_window is not null) await _window.RefreshFromTrayAsync();
        });

    private void TrayStopAll_Click(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(async void () =>
        {
            try { await Manager.StopAllAsync(); }
            catch { }
            if (_window is not null) await _window.RefreshFromTrayAsync();
        });

    private void TrayExit_Click(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(async void () =>
        {
            if (_window is not null) await _window.ExitAppAsync();
            else Shutdown();
        });

    internal static void Shutdown()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.Shutdown();
    }
}
