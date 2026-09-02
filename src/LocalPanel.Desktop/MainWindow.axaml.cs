using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LocalPanel.Core;
using LocalPanel.Services;

namespace LocalPanel.Desktop;

public partial class MainWindow : Window
{
    private readonly ServiceManager _manager;
    private bool _isRefreshing;
    private bool _pm2Loaded;
    private bool _reallyExit;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        _manager = App.Manager;
        VersionText.Text = "v" + App.Version;
        Loaded += async (_, _) => await RefreshAllAsync();
    }

    // ---------------- tray integration ----------------
    /// <summary>Bring the window back from the tray with fresh state.</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _ = RefreshAllAsync();
    }

    /// <summary>Lets the tray menu refresh the window after it changed service state.</summary>
    public Task RefreshFromTrayAsync() => RefreshAllAsync();

    /// <summary>Exit = stop every running service gracefully, then quit for real.</summary>
    public async Task ExitAppAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _reallyExit = true;
        Show();
        SetStatus("Stopping all services…");
        try { await _manager.StopAllAsync(); } catch { }
        App.Shutdown();
    }

    /// <summary>Closing the window hides it to the tray; Exit lives in the tray menu.
    /// Running localpanel-gui again brings this window back, so a desktop without a tray
    /// host still has a way to reach it.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    // ---------------- theme helpers ----------------
    private IBrush Res(string key) => (IBrush)this.FindResource(key)!;

    private void SetStatus(string text) => DownloadStatus.Text = text;

    /// <summary>Card container matching the Windows "Card" style.</summary>
    private Border Card(Control child, double bottomMargin = 14) => new()
    {
        Background = Res("CardBrush"),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(18),
        Margin = new Thickness(0, 0, 0, bottomMargin),
        Child = child,
    };

    /// <summary>A single row inside a card — dim separator underneath, like the WPF version table.</summary>
    private Border Row(Control child) => new()
    {
        BorderBrush = Res("BorderBrushDim"),
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(0, 10),
        Child = child,
    };

    private TextBlock Muted(string text, double size = 14) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Res("MutedBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Button Btn(string content, Action onClick, string? cls = null, double minWidth = 0)
    {
        var b = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center };
        if (cls != null) b.Classes.Add(cls);
        if (minWidth > 0) b.MinWidth = minWidth;
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------------- window chrome ----------------
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void WinMin_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void WinMax_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void WinClose_Click(object? sender, RoutedEventArgs e) => Close();

    // ---------------- navigation ----------------
    private void NavList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item) return;
        var tag = item.Tag as string ?? "dashboard";

        PageDashboard.IsVisible = tag == "dashboard";
        PageSites.IsVisible = tag == "sites";
        PagePhp.IsVisible = tag == "php";
        PageNode.IsVisible = tag == "node";
        PageMail.IsVisible = tag == "mail";
        PageServices.IsVisible = tag == "services";
        PagePm2.IsVisible = tag == "pm2";
        PageSettings.IsVisible = tag == "settings";

        PageTitle.Text = tag switch
        {
            "sites" => "Sites",
            "php" => "PHP",
            "node" => "Node",
            "mail" => "Mail",
            "services" => "Services",
            "pm2" => "PM2",
            "settings" => "Settings",
            _ => "Dashboard",
        };

        // PM2 shells out to node, so only pay for it when the page is actually opened.
        if (tag == "pm2" && !_pm2Loaded)
        {
            _pm2Loaded = true;
            _ = RefreshPm2Async();
        }
    }

    // ---------------- refresh ----------------
    private async Task RefreshAllAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            RefreshDashboard();
            RefreshServicesUi();
            RefreshPhpUi();
            RefreshSitesUi();
            RefreshMailUi();
            RefreshSettingsUi();
            await RefreshNodeUiAsync();
            await RefreshAvailablePhpAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    // ---------------- DASHBOARD ----------------
    private void RefreshDashboard()
    {
        ActiveServicesList.Children.Clear();
        var services = _manager.AllServices.Where(s => s.IsInstalled).ToList();
        NoActiveServices.IsVisible = services.Count == 0;

        foreach (var svc in services)
        {
            var running = svc.IsRunning;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6) };
            row.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = running ? Res("OkBrush") : Res("IdleDotBrush"),
            });
            row.Children.Add(new TextBlock
            {
                Text = svc.DisplayName,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = running ? Res("TextBrush") : Res("MutedBrush"),
            });
            row.Children.Add(Muted(running ? svc.PortInfo : "stopped", 13));
            ((TextBlock)row.Children[^1]).Margin = new Thickness(12, 0, 0, 0);
            ActiveServicesList.Children.Add(row);
        }

        var phpVersions = _manager.PhpPools.Select(p => p.Settings.Version).ToList();
        GlobalPhpCombo.ItemsSource = phpVersions;
        GlobalPhpCombo.SelectedItem = _manager.Config.DefaultPhpVersion;

        var nodeVersions = Paths.VersionDirs(Paths.NodeDir).Select(Path.GetFileName).ToList();
        GlobalNodeCombo.ItemsSource = nodeVersions;
        GlobalNodeCombo.SelectedItem = _manager.Config.CurrentNodeVersion;
    }

    private async void DashAll_Click(object? sender, RoutedEventArgs e)
    {
        DashAllBtn.IsEnabled = false;
        SetStatus("Starting services…");
        try
        {
            await _manager.StartAutoStartServicesAsync();
            SetStatus("Services started.");
        }
        catch (Exception ex)
        {
            SetStatus("Start failed: " + ex.Message);
        }
        finally
        {
            DashAllBtn.IsEnabled = true;
            await RefreshAllAsync();
        }
    }

    private void OnGlobalPhpChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing) return;
        if (GlobalPhpCombo.SelectedItem is string ver && ver != _manager.Config.DefaultPhpVersion)
        {
            _manager.SetCurrentPhp(ver);
            SetStatus($"Global PHP set to {ver}.");
            _ = RefreshAllAsync();
        }
    }

    private void OnGlobalNodeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing) return;
        if (GlobalNodeCombo.SelectedItem is string ver && ver != _manager.Config.CurrentNodeVersion)
        {
            _manager.SetCurrentNode(ver);
            SetStatus($"Global Node set to v{ver}.");
            _ = RefreshAllAsync();
        }
    }

    // ---------------- SERVICES ----------------
    private void RefreshServicesUi()
    {
        ServicesContainer.Children.Clear();

        foreach (var svc in _manager.AllServices)
        {
            var installed = svc.IsInstalled;
            var running = svc.IsRunning;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Fill = running ? Res("OkBrush") : Res("IdleDotBrush"),
            });
            head.Children.Add(new TextBlock
            {
                Text = svc.DisplayName,
                FontSize = 17,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            left.Children.Add(head);
            left.Children.Add(new TextBlock
            {
                Text = !installed
                    ? "Not installed"
                    : (running ? $"Running · {svc.PortInfo}" : $"Stopped · {svc.PortInfo}"),
                FontSize = 13,
                Foreground = Res("MutedBrush"),
                Margin = new Thickness(19, 4, 0, 0),
            });
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (installed)
            {
                var auto = new CheckBox
                {
                    Content = "Auto",
                    IsChecked = svc.AutoStart,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                };
                auto.Click += (_, _) =>
                {
                    svc.AutoStart = auto.IsChecked == true;
                    _manager.Config.Save();
                };
                actions.Children.Add(auto);

                var toggle = new Button
                {
                    Content = running ? "Stop" : "Start",
                    MinWidth = 84,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (running) toggle.Classes.Add("danger"); else toggle.Classes.Add("accent");
                toggle.Click += async (_, _) =>
                {
                    toggle.IsEnabled = false;
                    SetStatus($"{(running ? "Stopping" : "Starting")} {svc.DisplayName}…");
                    try
                    {
                        if (running) await svc.StopOffUiAsync();
                        else await svc.StartOffUiAsync();
                        SetStatus($"{svc.DisplayName} {(running ? "stopped" : "started")}.");
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"{svc.DisplayName}: {ex.Message}");
                    }
                    finally
                    {
                        await RefreshAllAsync();
                    }
                };
                actions.Children.Add(toggle);

                if (running)
                {
                    actions.Children.Add(Btn("Restart", async () =>
                    {
                        SetStatus($"Restarting {svc.DisplayName}…");
                        try { await svc.RestartAsync(); SetStatus($"{svc.DisplayName} restarted."); }
                        catch (Exception ex) { SetStatus($"{svc.DisplayName}: {ex.Message}"); }
                        await RefreshAllAsync();
                    }));
                }
            }
            else
            {
                var install = new Button
                {
                    Content = "Install",
                    MinWidth = 84,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                install.Classes.Add("accent");
                install.Click += async (_, _) =>
                {
                    install.IsEnabled = false;
                    SetStatus($"Looking up the latest {svc.DisplayName}…");
                    try
                    {
                        var remote = await LatestForServiceAsync(svc.Id);
                        if (remote == null)
                        {
                            SetStatus($"No download is published for {svc.DisplayName}.");
                            install.IsEnabled = true;
                            return;
                        }
                        await InstallAsync(install, svc.DisplayName, remote);
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"{svc.DisplayName}: {ex.Message}");
                        install.IsEnabled = true;
                    }
                };
                actions.Children.Add(install);
            }

            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            ServicesContainer.Children.Add(Card(grid));
        }
    }

    /// <summary>Newest published build for a managed service, or null if the catalog has none.</summary>
    private static async Task<RemoteVersion?> LatestForServiceAsync(string id) => id switch
    {
        "nginx" => (await VersionCatalog.GetNginxVersionsAsync()).FirstOrDefault(),
        "mysql" => (await VersionCatalog.GetMySqlVersionsAsync()).FirstOrDefault(),
        "redis" => (await VersionCatalog.GetRedisVersionsAsync()).FirstOrDefault(),
        "mailpit" => await VersionCatalog.GetMailpitAsync(),
        "mariadb" => (await VersionCatalog.GetMariaDbVersionsAsync()).FirstOrDefault(),
        "postgres" => (await VersionCatalog.GetPostgresVersionsAsync()).FirstOrDefault(),
        "mongodb" => (await VersionCatalog.GetMongoVersionsAsync()).FirstOrDefault(),
        _ => null,
    };

    // ---------------- PHP ----------------
    private void RefreshPhpUi()
    {
        InstalledPhpList.Children.Clear();
        var pools = _manager.PhpPools;
        if (pools.Count == 0)
        {
            InstalledPhpList.Children.Add(Muted("No PHP versions installed yet."));
            return;
        }

        foreach (var p in pools)
        {
            var isDefault = string.Equals(p.Settings.Version, _manager.Config.DefaultPhpVersion,
                                          StringComparison.OrdinalIgnoreCase);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,150,*") };

            var name = new TextBlock
            {
                Text = "PHP " + p.Settings.Version,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isDefault ? Res("OkBrush") : Res("TextBrush"),
                FontWeight = isDefault ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            Control middle = isDefault
                ? new TextBlock
                {
                    Text = "✓ default",
                    Foreground = Res("OkBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : Btn("Make default", () =>
                {
                    _manager.SetCurrentPhp(p.Settings.Version);
                    SetStatus($"Global PHP set to {p.Settings.Version}.");
                    _ = RefreshAllAsync();
                }, "accent", 110);
            Grid.SetColumn(middle, 1);
            grid.Children.Add(middle);

            var detail = Muted(p.IsRunning ? "FastCGI running" : "stopped", 14);
            Grid.SetColumn(detail, 2);
            grid.Children.Add(detail);

            InstalledPhpList.Children.Add(Row(grid));
        }
    }

    private async Task RefreshAvailablePhpAsync()
    {
        AvailablePhpList.Children.Clear();
        List<RemoteVersion> available;
        try
        {
            available = await VersionCatalog.GetPhpVersionsOnlineAsync() ?? VersionCatalog.GetPhpVersionsOffline();
        }
        catch
        {
            available = VersionCatalog.GetPhpVersionsOffline();
        }

        var installed = _manager.PhpPools.Select(p => p.Settings.Version).ToList();
        foreach (var remote in available)
        {
            var already = installed.Any(v => v.StartsWith(remote.Version.Split('.')[0] + "." +
                                                          remote.Version.Split('.').ElementAtOrDefault(1),
                                                          StringComparison.OrdinalIgnoreCase));
            AvailablePhpList.Children.Add(Row(BuildInstallRow($"PHP {remote.Version}", remote, already)));
        }
    }

    // ---------------- NODE ----------------
    private async Task RefreshNodeUiAsync()
    {
        var installed = Paths.VersionDirs(Paths.NodeDir).Select(Path.GetFileName).ToList();

        InstalledNodeList.Children.Clear();
        if (installed.Count == 0)
        {
            InstalledNodeList.Children.Add(Muted("No Node versions installed yet."));
        }
        else
        {
            foreach (var v in installed)
            {
                if (v == null) continue;
                var isCur = string.Equals(v, _manager.Config.CurrentNodeVersion, StringComparison.OrdinalIgnoreCase);
                var major = v.Split('.')[0];

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,150,*") };

                var name = new TextBlock
                {
                    Text = "Node v" + v,
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isCur ? Res("OkBrush") : Res("TextBrush"),
                    FontWeight = isCur ? FontWeight.SemiBold : FontWeight.Normal,
                };
                Grid.SetColumn(name, 0);
                grid.Children.Add(name);

                Control middle = isCur
                    ? new TextBlock
                    {
                        Text = "✓ active",
                        Foreground = Res("OkBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                    : Btn("Use", () =>
                    {
                        _manager.SetCurrentNode(v);
                        SetStatus($"Global Node set to v{v}.");
                        _ = RefreshAllAsync();
                    }, "accent", 110);
                Grid.SetColumn(middle, 1);
                grid.Children.Add(middle);

                var detail = Muted($"shims: node{major}, npm{major}, npx{major}", 14);
                Grid.SetColumn(detail, 2);
                grid.Children.Add(detail);

                InstalledNodeList.Children.Add(Row(grid));
            }
        }

        AvailableNodeList.Children.Clear();
        List<RemoteVersion> available;
        try
        {
            available = await VersionCatalog.GetNodeVersionsOnlineAsync() ?? VersionCatalog.GetNodeVersionsOffline();
        }
        catch
        {
            available = VersionCatalog.GetNodeVersionsOffline();
        }

        foreach (var remote in available)
        {
            var already = installed.Contains(remote.Version);
            AvailableNodeList.Children.Add(Row(BuildInstallRow($"Node v{remote.Version}", remote, already)));
        }
    }

    /// <summary>Shared "name — [Install] — detail" row used by the PHP and Node catalogs.</summary>
    private Grid BuildInstallRow(string label, RemoteVersion remote, bool alreadyInstalled)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,150,*") };

        var name = new TextBlock
        {
            Text = label,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        Control middle;
        if (alreadyInstalled)
        {
            middle = new TextBlock
            {
                Text = "✓ installed",
                Foreground = Res("OkBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            var btn = new Button
            {
                Content = "Install",
                MinWidth = 110,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Classes.Add("accent");
            btn.Click += async (_, _) => await InstallAsync(btn, label, remote);
            middle = btn;
        }
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        return grid;
    }

    private async Task InstallAsync(Button btn, string label, RemoteVersion remote)
    {
        btn.IsEnabled = false;
        btn.Content = "Installing…";
        DownloadProgress.Value = 0;
        SetStatus($"Downloading {label}…");

        var progress = new Progress<(long received, long total)>(p =>
        {
            if (p.total > 0)
                DownloadProgress.Value = p.received * 100.0 / p.total;
        });

        try
        {
            await _manager.InstallAsync(remote, progress);
            SetStatus($"{label} installed.");
        }
        catch (Exception ex)
        {
            SetStatus($"{label} failed: {ex.Message}");
        }
        finally
        {
            DownloadProgress.Value = 0;
            await RefreshAllAsync();
        }
    }

    // ---------------- SITES ----------------
    private void RefreshSitesUi()
    {
        ParkedList.Children.Clear();
        foreach (var parked in _manager.Config.ParkedPaths)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var path = new TextBlock
            {
                Text = parked,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(path, parked);
            Grid.SetColumn(path, 0);
            grid.Children.Add(path);

            var remove = Btn("✕", () =>
            {
                _manager.Config.ParkedPaths.Remove(parked);
                _manager.Config.Save();
                _manager.ImportParkedSites();
                RefreshSitesUi();
            });
            remove.Padding = new Thickness(9, 4);
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);

            ParkedList.Children.Add(Row(grid));
        }
        if (_manager.Config.ParkedPaths.Count == 0)
            ParkedList.Children.Add(Muted("No parked folders yet.", 13));

        SitesContainer.Children.Clear();
        var sites = _manager.Config.Sites;
        if (sites.Count == 0)
        {
            SitesContainer.Children.Add(Muted("No parked sites yet. Add a folder, then press Sync."));
            return;
        }

        foreach (var site in sites)
        {
            var isSsl = NginxConfigGenerator.HasCert(site);
            var url = $"{(isSsl ? "https" : "http")}://{site.Domain}";

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = site.Domain,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (isSsl)
            {
                titleRow.Children.Add(new TextBlock
                {
                    Text = "\U0001F512 SSL",
                    FontSize = 12,
                    Foreground = Res("OkBrush"),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            info.Children.Add(titleRow);
            info.Children.Add(new TextBlock
            {
                Text = $"{site.ResolvedRoot}  ·  PHP {site.PhpVersion ?? "default"}",
                FontSize = 12,
                Foreground = Res("MutedBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var open = Btn("Open ↗", () =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
                catch (Exception ex) { SetStatus("Could not open browser: " + ex.Message); }
            });
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);

            SitesContainer.Children.Add(Card(grid, 10));
        }
    }

    private async void AddParked_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Park a folder",
            AllowMultiple = false,
        });

        var folder = picked.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(folder)) return;

        if (!_manager.Config.ParkedPaths.Contains(folder))
        {
            _manager.Config.ParkedPaths.Add(folder);
            _manager.Config.Save();
            _manager.ImportParkedSites();
            SetStatus($"Parked {folder}.");
        }
        RefreshSitesUi();
    }

    private async void OnSyncSitesClicked(object? sender, RoutedEventArgs e)
    {
        SetStatus("Syncing sites, hosts and SSL…");
        try
        {
            _manager.ImportParkedSites();
            await _manager.ApplySitesAsync();
            SetStatus("Sites synced.");
        }
        catch (Exception ex)
        {
            SetStatus("Sync failed: " + ex.Message);
        }
        RefreshSitesUi();
    }

    // ---------------- MAIL ----------------
    private void RefreshMailUi()
    {
        var mail = _manager.Mailpit;
        if (!mail.IsInstalled)
        {
            MailStatusText.Text = "Mailpit is not installed yet.";
            MailToggleBtn.IsEnabled = false;
            MailOpenBtn.IsEnabled = false;
        }
        else
        {
            var running = mail.IsRunning;
            MailStatusText.Text = running
                ? $"Running · inbox at {mail.UiUrl}"
                : "Stopped · catches every email your sites send.";
            MailToggleBtn.Content = running ? "Stop" : "Start";
            MailToggleBtn.IsEnabled = true;
            MailOpenBtn.IsEnabled = running;
        }

        MailSmtpText.Text =
            $"Host: 127.0.0.1\nPort: {_manager.Config.Mailpit.SmtpPort}\n" +
            "Username / password: not required\nEncryption: none\n\n" +
            "Point your app's mailer at these values and every message lands in the Mailpit inbox " +
            "instead of going out to the internet.";
    }

    private async void MailToggle_Click(object? sender, RoutedEventArgs e)
    {
        var mail = _manager.Mailpit;
        var running = mail.IsRunning;
        MailToggleBtn.IsEnabled = false;
        SetStatus($"{(running ? "Stopping" : "Starting")} Mailpit…");
        try
        {
            if (running) await mail.StopOffUiAsync();
            else await mail.StartOffUiAsync();
            SetStatus($"Mailpit {(running ? "stopped" : "started")}.");
        }
        catch (Exception ex)
        {
            SetStatus("Mailpit: " + ex.Message);
        }
        await RefreshAllAsync();
    }

    private void MailOpen_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _manager.Mailpit.UiUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open inbox: " + ex.Message);
        }
    }

    // ---------------- PM2 ----------------
    private void Pm2Refresh_Click(object? sender, RoutedEventArgs e) => _ = RefreshPm2Async();

    private async Task RefreshPm2Async()
    {
        Pm2List.Children.Clear();
        Pm2StatusText.Text = "Loading…";

        if (!_manager.Pm2.Locate())
        {
            Pm2StatusText.Text = "PM2 not found. Install it with: npm install -g pm2";
            Pm2List.Children.Add(Muted("Nothing to show until PM2 is installed."));
            return;
        }

        List<Pm2Process> procs;
        try
        {
            procs = await _manager.Pm2.ListAsync();
        }
        catch (Exception ex)
        {
            Pm2StatusText.Text = "PM2 error: " + ex.Message;
            return;
        }

        Pm2StatusText.Text = _manager.Pm2.DaemonRunning
            ? $"Daemon running · {procs.Count} process(es)"
            : "Daemon not running";

        if (procs.Count == 0)
        {
            Pm2List.Children.Add(Muted("No PM2 processes registered."));
            return;
        }

        foreach (var proc in procs)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Fill = proc.IsOnline ? Res("OkBrush") : Res("IdleDotBrush"),
            });
            head.Children.Add(new TextBlock
            {
                Text = proc.Name,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            info.Children.Add(head);
            info.Children.Add(new TextBlock
            {
                Text = $"{proc.StatsText}   {proc.PortText}   restarts: {proc.Restarts}",
                FontSize = 12,
                Foreground = Res("MutedBrush"),
                Margin = new Thickness(19, 4, 0, 0),
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            actions.Children.Add(Btn(proc.ToggleText, async () =>
            {
                if (proc.IsOnline) await _manager.Pm2.StopAsync(proc.Id);
                else await _manager.Pm2.StartAppAsync(proc.Id);
                await RefreshPm2Async();
            }, proc.IsOnline ? "danger" : "accent", 84));
            actions.Children.Add(Btn("Restart", async () =>
            {
                await _manager.Pm2.RestartAsync(proc.Id);
                await RefreshPm2Async();
            }));
            actions.Children.Add(Btn("Delete", async () =>
            {
                await _manager.Pm2.DeleteAsync(proc.Id);
                await RefreshPm2Async();
            }));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            Pm2List.Children.Add(Row(grid));
        }
    }

    // ---------------- SETTINGS ----------------
    private void RefreshSettingsUi()
    {
        TldInput.Text = _manager.Config.Tld;
        AutoStartCheck.IsChecked = _manager.Config.AutoStartServicesOnLaunch;
        CheckUpdatesCheck.IsChecked = _manager.Config.CheckUpdatesOnStart;
    }

    private void SaveTld_Click(object? sender, RoutedEventArgs e)
    {
        var tld = (TldInput.Text ?? "").Trim().TrimStart('.');
        if (tld.Length == 0)
        {
            SetStatus("TLD cannot be empty.");
            return;
        }
        _manager.Config.Tld = tld;
        _manager.Config.Save();
        SetStatus($"TLD saved as .{tld} — press Sync on the Sites page to apply.");
    }

    private void AutoStartCheck_Click(object? sender, RoutedEventArgs e)
    {
        _manager.Config.AutoStartServicesOnLaunch = AutoStartCheck.IsChecked == true;
        _manager.Config.Save();
    }

    private void CheckUpdatesCheck_Click(object? sender, RoutedEventArgs e)
    {
        _manager.Config.CheckUpdatesOnStart = CheckUpdatesCheck.IsChecked == true;
        _manager.Config.Save();
    }

    private async void OnUpdateAppClicked(object? sender, RoutedEventArgs e)
    {
        SetStatus("Checking for updates…");
        try
        {
            var tag = await AppUpdater.CheckAndDownloadAsync();
            if (tag == null)
            {
                SetStatus("You are already on the latest version.");
                return;
            }
            SetStatus($"Update {tag} downloaded — restarting to apply…");
            AppUpdater.ApplyDownloadedUpdate();
        }
        catch (Exception ex)
        {
            SetStatus("Update failed: " + ex.Message);
        }
    }
}
