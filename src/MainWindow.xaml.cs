using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LocalPanel.Core;
using LocalPanel.UI;

namespace LocalPanel;

public partial class MainWindow : Window
{
    private ServiceManager Manager => App.Manager;
    private readonly ObservableCollection<ServiceRow> _serviceRows = new();
    private readonly ObservableCollection<VersionRow> _phpRows = new();
    private readonly ObservableCollection<VersionRow> _nodeRows = new();
    private readonly DispatcherTimer _refreshTimer;
    private List<RemoteVersion> _nginxCatalog = new(), _mysqlCatalog = new(), _redisCatalog = new(), _pmaCatalog = new();
    private List<RemoteVersion> _phpCatalog = new(), _nodeCatalog = new();
    private RemoteVersion? _mkcertCatalog, _composerCatalog;
    private bool _uiLoading = true;
    private TrayManager? _tray;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();

        ServicesList.ItemsSource = _serviceRows;
        PhpVersionsList.ItemsSource = _phpRows;
        NodeVersionsList.ItemsSource = _nodeRows;
        RebuildServiceRows();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();

        SiteType.ItemsSource = new[] { "PHP", "Static", "Proxy" };
        UpstreamKind.ItemsSource = new[] { "Port", "Socket" };
        VersionText.Text = "LocalPanel v" + App.Version;

        LoadSettingsFields();
        RefreshSitesList();
        RefreshPhpCombos();
        RefreshComponentSummaries();
        RefreshStatus();
        _uiLoading = false;

        _ = LoadCatalogsAsync();
        _ = Pm2RefreshAsync();

        _tray = new TrayManager(
            open: () => Dispatcher.Invoke(ShowFromTray),
            startAll: () => Dispatcher.InvokeAsync(async () =>
            {
                await Manager.StartAutoStartServicesAsync();
                RefreshStatus();
            }).Task,
            stopAll: () => Dispatcher.InvokeAsync(async () =>
            {
                await Manager.StopAllAsync();
                RefreshStatus();
            }).Task,
            exit: () => Dispatcher.Invoke(ExitApp));

        if (Manager.Config.StartMinimizedToTray)
            Loaded += (_, _) => Hide();

        if (Manager.Config.CheckUpdatesOnStart)
        {
            _ = Task.Run(async () =>
            {
                var tag = await AppUpdater.CheckAndDownloadAsync();
                if (tag is null) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (MessageBox.Show(
                            $"LocalPanel {tag} is available (you have {App.Version}).\n\nUpdate and restart now?",
                            "LocalPanel — update", MessageBoxButton.YesNo, MessageBoxImage.Information)
                        == MessageBoxResult.Yes)
                    {
                        AppUpdater.ApplyDownloadedUpdate();
                        ExitApp();
                    }
                });
            });
        }

        // --page <tag> opens directly on that page (also handy for testing).
        var args = Environment.GetCommandLineArgs();
        var pageIdx = Array.IndexOf(args, "--page");
        if (pageIdx >= 0 && pageIdx + 1 < args.Length)
        {
            var tag = args[pageIdx + 1].ToLowerInvariant();
            foreach (ListBoxItem item in NavList.Items)
                if ((string)item.Tag == tag) { item.IsSelected = true; break; }
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private bool _exiting;

    /// <summary>Exit = stop every running service gracefully, then quit.</summary>
    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _reallyExit = true;
        _tray?.Notify("Stopping all services…");
        try { await Manager.StopAllAsync(); } catch { }
        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
    }

    /// <summary>Closing the window hides to the tray; Exit lives in the tray menu.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            _tray.Notify("LocalPanel is still running here — services keep running in the background.");
            return;
        }
        base.OnClosing(e);
    }

    // Dark title bar (Windows 10 1809+)
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ==================== Custom title bar ====================

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        try { DragMove(); } catch { /* only valid while the button is down */ }
    }

    private void WinMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void WinMax_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void WinClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Without a real caption, a maximized borderless window overflows the
        // screen by the resize border — compensate with a margin.
        RootLayout.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    // ==================== Navigation ====================

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item || PageDashboard is null) return;
        var tag = (string)item.Tag;

        PageDashboard.Visibility = Vis(tag == "dashboard");
        PageSites.Visibility = Vis(tag == "sites");
        PagePhp.Visibility = Vis(tag == "php");
        PageNode.Visibility = Vis(tag == "node");
        PageServices.Visibility = Vis(tag == "services");
        PagePm2.Visibility = Vis(tag == "pm2");
        PageSettings.Visibility = Vis(tag == "settings");

        PageTitle.Text = tag switch
        {
            "dashboard" => "Dashboard",
            "sites" => "Sites",
            "php" => "PHP",
            "node" => "Node",
            "services" => "Services",
            "pm2" => "PM2",
            "settings" => "Settings",
            _ => "LocalPanel"
        };

        if (tag == "pm2") _ = Pm2RefreshAsync();
        static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== Status refresh ====================

    private void RefreshStatus()
    {
        foreach (var r in _serviceRows) r.Refresh();
        var active = _serviceRows.Where(r => r.IsRunning).ToList();
        ActiveServicesList.ItemsSource = active;
        NoActiveServices.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildServiceRows()
    {
        _serviceRows.Clear();
        foreach (var svc in Manager.AllServices)
        {
            var row = new ServiceRow(svc);
            row.Refresh();
            _serviceRows.Add(row);
        }
    }

    // ==================== Dashboard ====================

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        DownloadStatus.Text = "Stopping all services…";
        await Manager.StopAllAsync();
        RefreshStatus();
        DownloadStatus.Text = "All services stopped.";
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        DownloadStatus.Text = "Starting enabled services…";
        await Manager.StartAutoStartServicesAsync((name, ex) =>
            MessageBox.Show($"{name}: {ex.Message}", "LocalPanel", MessageBoxButton.OK, MessageBoxImage.Warning));
        RefreshStatus();
        DownloadStatus.Text = "Done.";
    }


    private async void GlobalPhpCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiLoading || GlobalPhpCombo.SelectedItem is not string v) return;
        await Task.Run(() => Manager.SetCurrentPhp(v));
        RefreshVersionPages();
        DownloadStatus.Text = $"PHP {v} is now the global version (php + composer follow it).";
    }

    private async void GlobalNodeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiLoading || GlobalNodeCombo.SelectedItem is not string v) return;
        await Task.Run(() => Manager.SetCurrentNode(v));
        RefreshVersionPages();
        DownloadStatus.Text = $"Node {v} is now the global version (node/npm/npx follow it).";
    }

    // ==================== Version pages (PHP / Node) ====================

    private async Task LoadCatalogsAsync()
    {
        // Instant: cached/seed lists render immediately — no network wait.
        _phpCatalog = VersionCatalog.GetPhpVersionsOffline();
        _nodeCatalog = VersionCatalog.GetNodeVersionsOffline();
        RefreshVersionPages();
        DownloadStatus.Text = "Checking online for newer versions…";

        try
        {
            var nginx = VersionCatalog.GetNginxVersionsAsync();
            var mysql = VersionCatalog.GetMySqlVersionsAsync();
            var redis = VersionCatalog.GetRedisVersionsAsync();
            var pma = VersionCatalog.GetPhpMyAdminVersionsAsync();
            var mkcert = VersionCatalog.GetMkcertAsync();
            await Task.WhenAll(nginx, mysql, redis, pma, mkcert);
            _nginxCatalog = await nginx;
            _mysqlCatalog = await mysql;
            _redisCatalog = await redis;
            _pmaCatalog = await pma;
            _mkcertCatalog = await mkcert;
            _composerCatalog = await VersionCatalog.GetComposerAsync();

            // Background refresh: only replace the lists when the fetch succeeds.
            var php = await VersionCatalog.GetPhpVersionsOnlineAsync();
            var node = await VersionCatalog.GetNodeVersionsOnlineAsync();
            if (php is not null) _phpCatalog = php;
            if (node is not null) _nodeCatalog = node;
            RefreshVersionPages();
            DownloadStatus.Text = php is null
                ? "Ready (offline catalog — php.net unreachable right now)."
                : "Ready.";
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = "Ready (offline catalog: " + ex.Message + ")";
        }
    }

    // FastCGI ports are internal plumbing — nothing to show per version.
    private string PhpDetail(string version) => "";

    /// <summary>Rebuild the PHP and Node version tables from the cached catalogs.</summary>
    private void RefreshVersionPages()
    {
        BuildVersionRows(_phpRows, _phpCatalog, "php", Paths.PhpDir, PhpDetail,
            branchOf: v => string.Join('.', v.Split('.').Take(2)),   // 8.3.33 -> 8.3
            current: Manager.Config.DefaultPhpVersion);
        BuildVersionRows(_nodeRows, _nodeCatalog, "node", Paths.NodeDir, _ => "",
            branchOf: v => v.Split('.')[0],                          // 20.20.2 -> 20
            current: Manager.Config.CurrentNodeVersion);
    }

    private static string PadVersion(string v)
        => string.Join(".", v.Split('.').Select(p => p.PadLeft(6, '0')));

    private void BuildVersionRows(ObservableCollection<VersionRow> rows, List<RemoteVersion> catalog,
        string component, string installDir, Func<string, string> detail,
        Func<string, string> branchOf, string? current)
    {
        rows.Clear();
        var installed = Paths.VersionDirs(installDir)
            .Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToHashSet();

        var built = new List<VersionRow>();
        foreach (var v in catalog)
        {
            var exact = installed.Contains(v.Version);
            string? updateFrom = null;
            if (!exact)
                updateFrom = installed
                    .Where(i => branchOf(i) == branchOf(v.Version) &&
                                string.CompareOrdinal(PadVersion(i), PadVersion(v.Version)) < 0)
                    .OrderByDescending(PadVersion)
                    .FirstOrDefault();
            built.Add(new VersionRow(v, exact, updateFrom, exact && v.Version == current,
                exact ? detail(v.Version) : ""));
        }

        // Installed versions that are not an exact catalog match still show up (✓ / Use).
        foreach (var v in installed.Where(i => catalog.All(c => c.Version != i)))
            built.Add(new VersionRow(new RemoteVersion(component, v, ""), true, null, v == current, detail(v)));

        foreach (var row in built.OrderByDescending(r => PadVersion(r.Version), StringComparer.Ordinal))
        {
            row.ShowSettings = component == "php" && row.Installed;
            rows.Add(row);
        }
    }

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        var row = (VersionRow)((FrameworkElement)sender).DataContext;
        row.Busy = true;
        var spinner = ButtonSpinner.Start((Button)sender);
        try
        {
            if (row.UpdateFrom is not null && row.Remote.Component == "php")
            {
                // In-place update: settings + sites move to the new patch, old folder is removed.
                DownloadStatus.Text = $"Updating PHP {row.UpdateFrom} → {row.Version}…";
                await Manager.UpdatePhpAsync(row.Remote, row.UpdateFrom, InstallProgress(row.Remote));
                DownloadProgress.Value = 0;
                DownloadStatus.Text = $"PHP updated to {row.Version}.";
            }
            else
            {
                await InstallWithProgress(row.Remote);
                if (row.UpdateFrom is not null && row.Remote.Component == "node" &&
                    Manager.Config.CurrentNodeVersion == row.UpdateFrom)
                    Manager.SetCurrentNode(row.Version);
            }
            RebuildServiceRows();
            RefreshPhpCombos();
            RefreshVersionPages();
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = (row.UpdateFrom is null ? "Install" : "Update") + " failed: " + ex.Message;
        }
        finally { spinner.Dispose(); row.Busy = false; }
    }


    private Progress<(long done, long total)> InstallProgress(RemoteVersion version)
        => new(p =>
        {
            if (p.total > 0)
            {
                DownloadProgress.Value = 100.0 * p.done / p.total;
                DownloadStatus.Text = $"Downloading {version.Component} {version.Version}: " +
                                      $"{p.done / 1048576} / {p.total / 1048576} MB";
            }
        });

    private async Task InstallWithProgress(RemoteVersion version)
    {
        DownloadStatus.Text = $"Downloading {version.Component} {version.Version}…";
        await Manager.InstallAsync(version, InstallProgress(version));
        DownloadProgress.Value = 0;
        DownloadStatus.Text = $"{version.Component} {version.Version} installed.";
        RefreshComponentSummaries();
    }

    // ==================== Services page ====================

    private static ServiceRow RowOf(object sender) => (ServiceRow)((FrameworkElement)sender).DataContext;

    private async void ServiceStart_Click(object sender, RoutedEventArgs e) => await RunRow(RowOf(sender), r => r.Service.StartAsync());
    private async void ServiceStop_Click(object sender, RoutedEventArgs e) => await RunRow(RowOf(sender), r => r.Service.StopAsync());
    private async void ServiceRestart_Click(object sender, RoutedEventArgs e) => await RunRow(RowOf(sender), r => r.Service.RestartAsync());

    private void ServiceLog_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (!File.Exists(row.Service.LogFile))
        {
            MessageBox.Show("No log yet for " + row.Name, "LocalPanel");
            return;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{row.Service.LogFile}\"") { UseShellExecute = true });
    }

    private async Task RunRow(ServiceRow row, Func<ServiceRow, Task> action)
    {
        row.Busy = true;
        try { await action(row); }
        catch (Exception ex) { MessageBox.Show(ex.Message, row.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { row.Busy = false; RefreshStatus(); }
    }

    private void RefreshComponentSummaries()
    {
        Set(CompNginx, CompNginxBtn, File.Exists(Path.Combine(Paths.NginxDir, "nginx.exe")));
        Set(CompMysql, CompMysqlBtn, Manager.MySql.IsInstalled);
        Set(CompRedis, CompRedisBtn, Manager.Redis.IsInstalled);
        Set(CompPma, CompPmaBtn, Directory.Exists(Paths.PhpMyAdminDir));
        Set(CompMkcert, CompMkcertBtn, SslManager.IsInstalled);
        Set(CompComposer, CompComposerBtn, File.Exists(Paths.ComposerPhar));

        static void Set(TextBlock label, Button btn, bool installed)
        {
            label.Text = installed ? "Installed" : "Not installed";
            btn.IsEnabled = !installed;
            btn.Content = installed ? "Installed ✓" : "Install";
        }
    }

    private async void InstallComponent_Click(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag;
        var version = key switch
        {
            "nginx" => _nginxCatalog.FirstOrDefault(),
            "mysql" => _mysqlCatalog.FirstOrDefault(),
            "redis" => _redisCatalog.FirstOrDefault(),
            "phpmyadmin" => _pmaCatalog.FirstOrDefault(),
            "mkcert" => _mkcertCatalog,
            "composer" => _composerCatalog,
            _ => null
        };
        if (version is null)
        {
            DownloadStatus.Text = "Catalog not loaded yet — try again in a moment.";
            return;
        }
        var btn = (Button)sender;
        var spinner = ButtonSpinner.Start(btn);
        try
        {
            await InstallWithProgress(version);
            RebuildServiceRows();
        }
        catch (Exception ex) { DownloadStatus.Text = "Install failed: " + ex.Message; }
        finally
        {
            spinner.Dispose();
            RefreshComponentSummaries(); // re-lock the button if it is now installed
        }
    }

    // ==================== PHP settings ====================

    private void PhpSetVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PhpSetVersion.SelectedItem is not string v) return;
        var s = Manager.Config.Php.FirstOrDefault(p => p.Version == v);
        if (s is null) return;
        PhpSetMemory.Text = s.MemoryLimitMb.ToString();
        PhpSetUpload.Text = s.UploadMaxMb.ToString();
        PhpSetExec.Text = s.MaxExecutionSeconds.ToString();
    }

    private async void PhpSettingsSave_Click(object sender, RoutedEventArgs e)
    {
        if (PhpSetVersion.SelectedItem is not string v)
        {
            PhpSetStatus.Text = "Install a PHP version first.";
            return;
        }
        var s = Manager.Config.Php.First(p => p.Version == v);
        if (int.TryParse(PhpSetMemory.Text, out var mem) && mem > 0) s.MemoryLimitMb = mem;
        if (int.TryParse(PhpSetUpload.Text, out var up) && up > 0) s.UploadMaxMb = up;
        if (int.TryParse(PhpSetExec.Text, out var ex) && ex > 0) s.MaxExecutionSeconds = ex;
        Manager.Config.Save();

        var pool = Manager.PhpPools.FirstOrDefault(p => p.Settings.Version == v);
        if (pool is null || !pool.IsInstalled)
        {
            PhpSetStatus.Text = "Saved.";
            return;
        }
        pool.WriteIni();
        if (pool.IsRunning)
        {
            PhpSetStatus.Text = $"Saved — restarting PHP {v} pool…";
            await pool.RestartAsync();
            RefreshStatus();
            PhpSetStatus.Text = $"Saved — PHP {v} pool restarted with the new settings.";
        }
        else
        {
            PhpSetStatus.Text = "Saved — will apply on next start.";
        }
    }

    private void PhpOpenIni_Click(object sender, RoutedEventArgs e)
    {
        if (PhpSetVersion.SelectedItem is not string v)
        {
            PhpSetStatus.Text = "Install a PHP version first.";
            return;
        }
        var pool = Manager.PhpPools.FirstOrDefault(p => p.Settings.Version == v);
        pool?.EnsureIni();
        var ini = Path.Combine(Paths.PhpEtc, $"php-{v}.ini");
        if (!File.Exists(ini)) { PhpSetStatus.Text = "No ini generated yet."; return; }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{ini}\"") { UseShellExecute = true });
    }

    /// <summary>Per-row Settings button: load that version into the PHP Settings card.</summary>
    private void VersionSettings_Click(object sender, RoutedEventArgs e)
    {
        var row = (VersionRow)((FrameworkElement)sender).DataContext;
        PhpSetVersion.SelectedItem = row.Version;
        PhpSettingsCard.BringIntoView();
    }

    // ==================== Sites ====================

    private void RefreshSitesList()
    {
        SitesListBox.ItemsSource = null;
        SitesListBox.ItemsSource = Manager.Config.Sites;
    }

    private void RefreshPhpCombos()
    {
        var versions = Manager.Config.Php.Select(p => p.Version).ToList();
        SitePhpVersion.ItemsSource = versions;
        var loading = _uiLoading;
        _uiLoading = true;
        GlobalPhpCombo.ItemsSource = versions;
        GlobalPhpCombo.SelectedItem = Manager.Config.DefaultPhpVersion;
        var keep = PhpSetVersion.SelectedItem as string;
        PhpSetVersion.ItemsSource = versions;
        PhpSetVersion.SelectedItem = keep is not null && versions.Contains(keep)
            ? keep : Manager.Config.DefaultPhpVersion;

        var nodeVersions = Paths.VersionDirs(Paths.NodeDir)
            .Select(Path.GetFileName).Where(n => n is not null).Cast<string>()
            .OrderByDescending(PadVersion).ToList();
        GlobalNodeCombo.ItemsSource = nodeVersions;
        GlobalNodeCombo.SelectedItem = Manager.Config.CurrentNodeVersion;
        _uiLoading = loading;
    }

    private readonly List<string> _editAliases = new();

    private void RefreshAliasChips()
    {
        AliasChips.ItemsSource = null;
        AliasChips.ItemsSource = _editAliases.ToList();
    }

    private void AliasAdd_Click(object sender, RoutedEventArgs e) => AddAliasFromInput();

    private void AliasInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddAliasFromInput(); e.Handled = true; }
    }

    private void AddAliasFromInput()
    {
        var alias = AliasInput.Text.Trim().ToLowerInvariant();
        if (alias.Length == 0) return;
        if (!alias.Contains('.')) alias += "." + Manager.Config.Tld;
        if (!_editAliases.Contains(alias)) _editAliases.Add(alias);
        AliasInput.Text = "";
        RefreshAliasChips();
        AliasInput.Focus();
    }

    private void AliasRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not string alias) return;
        _editAliases.Remove(alias);
        RefreshAliasChips();
    }

    private void SitesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SitesListBox.SelectedItem is not Site s) return;
        SiteDomain.Text = s.Domain;
        SiteRoot.Text = s.RootPath;
        SiteType.SelectedItem = s.Type switch
        {
            Core.SiteType.Static => "Static",
            Core.SiteType.Proxy => "Proxy",
            _ => "PHP"
        };
        SitePhpVersion.SelectedItem = s.PhpVersion ?? Manager.Config.DefaultPhpVersion;
        _editAliases.Clear();
        _editAliases.AddRange(s.Aliases);
        RefreshAliasChips();
        AliasInput.Text = "";
        SiteSsl.IsChecked = s.Ssl;
        UpstreamKind.SelectedItem = s.UpstreamKind == Core.UpstreamKind.Socket ? "Socket" : "Port";
        UpstreamPort.Text = s.UpstreamPort.ToString();
        UpstreamSocket.Text = s.UpstreamSocketPath ?? "";
    }

    private void SiteType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isProxy = (string?)SiteType.SelectedItem == "Proxy";
        ProxyPanel.Visibility = isProxy ? Visibility.Visible : Visibility.Collapsed;
        SitePhpVersion.IsEnabled = (string?)SiteType.SelectedItem == "PHP";
    }

    private void SiteNew_Click(object sender, RoutedEventArgs e)
    {
        SitesListBox.SelectedItem = null;
        SiteDomain.Text = "";
        SiteRoot.Text = "";
        SiteType.SelectedItem = "PHP";
        SitePhpVersion.SelectedItem = Manager.Config.DefaultPhpVersion;
        _editAliases.Clear();
        RefreshAliasChips();
        AliasInput.Text = "";
        SiteSsl.IsChecked = true;
        UpstreamKind.SelectedItem = "Port";
        UpstreamPort.Text = "3000";
        UpstreamSocket.Text = "";
        SiteDomain.Focus();
    }

    private void SiteSave_Click(object sender, RoutedEventArgs e)
    {
        var domain = SiteDomain.Text.Trim().ToLowerInvariant();
        if (domain.Length == 0)
        {
            MessageBox.Show("Domain is required.", "LocalPanel");
            return;
        }
        if (!domain.Contains('.')) domain += "." + Manager.Config.Tld;

        var site = SitesListBox.SelectedItem as Site
                   ?? Manager.Config.Sites.FirstOrDefault(s => s.Domain == domain);
        var isNew = site is null;
        site ??= new Site();

        var tldSuffix = "." + Manager.Config.Tld;
        site.Name = domain.EndsWith(tldSuffix) ? domain[..^tldSuffix.Length] : domain;
        site.Domain = domain;
        site.RootPath = SiteRoot.Text.Trim();
        site.Type = (string?)SiteType.SelectedItem switch
        {
            "Static" => Core.SiteType.Static,
            "Proxy" => Core.SiteType.Proxy,
            _ => Core.SiteType.Php
        };
        site.PhpVersion = SitePhpVersion.SelectedItem as string;
        var aliases = _editAliases.ToList();
        if (!site.Aliases.SequenceEqual(aliases))
            SslManager.InvalidateCertificate(domain); // reissue with the new alias list
        site.Aliases = aliases;
        site.Ssl = SiteSsl.IsChecked == true;
        site.UpstreamKind = (string?)UpstreamKind.SelectedItem == "Socket"
            ? Core.UpstreamKind.Socket : Core.UpstreamKind.Port;
        site.UpstreamPort = int.TryParse(UpstreamPort.Text, out var up) ? up : 3000;
        site.UpstreamSocketPath = string.IsNullOrWhiteSpace(UpstreamSocket.Text) ? null : UpstreamSocket.Text.Trim();

        if (isNew) Manager.Config.Sites.Add(site);
        Manager.Config.Save();
        RefreshSitesList();
        SitesListBox.SelectedItem = site;
        _ = ApplyAllAsync($"{site.Domain} saved — applying…", $"{site.Domain} is live.");
    }

    /// <summary>Saving/deleting applies everything automatically (hosts + SSL + nginx).</summary>
    private async Task ApplyAllAsync(string working, string done)
    {
        SitesStatus.Text = working;
        try
        {
            await Manager.ApplySitesAsync();
            RefreshSitesList();
            SitesStatus.Text = done;
        }
        catch (Exception ex)
        {
            SitesStatus.Text = "Apply failed: " + ex.Message;
        }
    }

    private void SiteBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick the site's document root" };
        if (dlg.ShowDialog(this) == true) SiteRoot.Text = dlg.FolderName;
    }

    private void SiteOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SitesListBox.SelectedItem is not Site s) return;
        var scheme = s.Ssl ? "https" : "http";
        Process.Start(new ProcessStartInfo($"{scheme}://{s.Domain}") { UseShellExecute = true });
    }

    // ==================== PM2 ====================

    private async void Pm2Refresh_Click(object sender, RoutedEventArgs e) => await Pm2RefreshAsync();

    private async Task Pm2RefreshAsync()
    {
        Pm2Grid.ItemsSource = await Manager.Pm2.ListAsync();
    }

    private Pm2Process? SelectedPm2 => Pm2Grid.SelectedItem as Pm2Process;

    private async void Pm2Stop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPm2 is { } p) { await Manager.Pm2.StopAsync(p.Id); await Pm2RefreshAsync(); }
    }

    private async void Pm2Restart_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPm2 is { } p) { await Manager.Pm2.RestartAsync(p.Id); await Pm2RefreshAsync(); }
    }

    private async void Pm2Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPm2 is not { } p) return;
        if (MessageBox.Show($"Delete PM2 process “{p.Name}” (id {p.Id})?", "LocalPanel",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Manager.Pm2.DeleteAsync(p.Id);
        await Pm2RefreshAsync();
    }

    private async void Pm2Logs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPm2 is { } p) Pm2LogBox.Text = await Manager.Pm2.LogsAsync(p.Id);
    }

    // ==================== Settings ====================

    private void LoadSettingsFields()
    {
        var c = Manager.Config;
        SetAutoLaunch.IsChecked = c.AutoStartServicesOnLaunch;
        SetLaunchAtLogin.IsChecked = StartupManager.IsEnabled;
        SetStartMinimized.IsChecked = c.StartMinimizedToTray;
        SetAutoUpdate.IsChecked = c.CheckUpdatesOnStart;
        SetTld.Text = c.Tld;
        RefreshParkedList();
        SetNginxHttp.Text = c.Nginx.HttpPort.ToString();
        SetNginxHttps.Text = c.Nginx.HttpsPort.ToString();
        SetMySqlPort.Text = c.MySql.Port.ToString();
        SetRedisPort.Text = c.Redis.Port.ToString();
    }

    private void RefreshParkedList()
    {
        ParkedList.ItemsSource = null;
        ParkedList.ItemsSource = Manager.Config.ParkedPaths.ToList();
    }

    private async void ParkedAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder to park (each subfolder becomes a site)" };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FolderName.TrimEnd('\\');
        if (Manager.Config.ParkedPaths.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;
        Manager.Config.ParkedPaths.Add(path);
        Manager.Config.Save();
        RefreshParkedList();
        SettingsStatus.Text = "Parked — importing sites…";
        try
        {
            await Manager.ApplySitesAsync();
            RefreshSitesList();
            SettingsStatus.Text = "Parked — sites imported and live.";
        }
        catch (Exception ex) { SettingsStatus.Text = "Parked, but apply failed: " + ex.Message; }
    }

    private void ParkedRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not string path) return;
        Manager.Config.ParkedPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        Manager.Config.Save();
        RefreshParkedList();
        SettingsStatus.Text = "Removed. Existing sites stay in Sites — delete them there if you want them gone.";
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var c = Manager.Config;
        c.AutoStartServicesOnLaunch = SetAutoLaunch.IsChecked == true;
        c.StartMinimizedToTray = SetStartMinimized.IsChecked == true;
        c.CheckUpdatesOnStart = SetAutoUpdate.IsChecked == true;
        try { StartupManager.SetEnabled(SetLaunchAtLogin.IsChecked == true); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not update the Windows startup entry: " + ex.Message,
                "LocalPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (SetTld.Text.Trim().Length > 0) c.Tld = SetTld.Text.Trim().TrimStart('.');
        if (int.TryParse(SetNginxHttp.Text, out var p1)) c.Nginx.HttpPort = p1;
        if (int.TryParse(SetNginxHttps.Text, out var p2)) c.Nginx.HttpsPort = p2;
        if (int.TryParse(SetMySqlPort.Text, out var p3)) c.MySql.Port = p3;
        if (int.TryParse(SetRedisPort.Text, out var p4)) c.Redis.Port = p4;
        c.Save();
        RefreshStatus();
        SettingsStatus.Text = "Saved. Restart the affected services to apply new ports.";
    }
}
