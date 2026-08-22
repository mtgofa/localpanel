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
    // Dashboard rows: every service except Mail (which has its own page).
    private readonly List<ServiceRow> _dashRows = new();
    private readonly ObservableCollection<ServiceRow> _dashVisible = new();
    private readonly ObservableCollection<VersionRow> _phpRows = new();
    private readonly ObservableCollection<VersionRow> _nodeRows = new();
    private readonly DispatcherTimer _refreshTimer;
    private List<RemoteVersion> _nginxCatalog = new(), _mysqlCatalog = new(), _redisCatalog = new(), _pmaCatalog = new();
    private List<RemoteVersion> _mariaCatalog = new(), _pgCatalog = new(), _mongoCatalog = new();
    private List<RemoteVersion> _phpCatalog = new(), _nodeCatalog = new();
    private RemoteVersion? _mkcertCatalog, _mailpitCatalog;
    private bool _uiLoading = true;
    private TrayManager? _tray;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();

        PhpVersionsList.ItemsSource = _phpRows;
        NodeVersionsList.ItemsSource = _nodeRows;
        RebuildServiceRows();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        // Only probe while the window is on screen — in the tray (the normal state) nothing reads it;
        // ShowFromTray refreshes explicitly so state is fresh the moment it reappears.
        _refreshTimer.Tick += (_, _) => { if (IsVisible) RefreshStatus(); };
        _refreshTimer.Start();

        RouteList.ItemsSource = _editRoutes;
        VersionText.Text = "LocalPanel v" + App.Version;

        LoadSettingsFields();
        RefreshSitesList();
        RefreshPhpCombos();
        RefreshStatus();
        _uiLoading = false;

        _ = LoadCatalogsAsync();
        // PM2 is probed when its page opens (NavList_SelectionChanged) — not at startup.

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
        RefreshStatus();
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
        PageMail.Visibility = Vis(tag == "mail");
        PageServices.Visibility = Vis(tag == "services");
        PagePm2.Visibility = Vis(tag == "pm2");
        PageSettings.Visibility = Vis(tag == "settings");

        PageTitle.Text = tag switch
        {
            "dashboard" => "Dashboard",
            "sites" => "Sites",
            "php" => "PHP",
            "node" => "Node",
            "mail" => "Mail",
            "services" => "Services",
            "pm2" => "PM2",
            "settings" => "Settings",
            _ => "LocalPanel"
        };

        if (tag == "pm2") _ = Pm2RefreshAsync();
        if (tag == "mail") RefreshMailPage(fillPorts: true);
        if (tag == "services") RefreshServicesPage(fillFields: true);
        static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== Status refresh ====================

    private void RefreshStatus()
    {
        foreach (var r in _dashRows) r.Refresh();
        // Dashboard shows every installed service — idle ones stay listed, dimmed.
        // Keep ONE bound collection and sync it in place: rebinding ItemsSource every
        // 2 s rebuilds the visual tree and flickers.
        var installed = _dashRows.Where(r => r.IsInstalled).ToList();
        if (!_dashVisible.SequenceEqual(installed))
        {
            _dashVisible.Clear();
            foreach (var r in installed) _dashVisible.Add(r);
        }
        if (ActiveServicesList.ItemsSource is null) ActiveServicesList.ItemsSource = _dashVisible;
        NoActiveServices.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var anyRunning = installed.Any(r => r.IsRunning);
        var want = anyRunning ? "Stop all services" : "Start all services";
        if (DashAllBtn.Content is not string s || s != want) DashAllBtn.Content = want;
        if (PageMail.Visibility == Visibility.Visible)
            RefreshMailQuick();
        if (PageServices.Visibility == Visibility.Visible)
            RefreshServicesQuick();
    }

    /// <summary>Idle-dot brush shared by every card (frozen: no per-tick allocation, no change tracking).</summary>
    private static readonly System.Windows.Media.SolidColorBrush IdleDotBrush = Frozen(
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x54, 0x70)));

    private static System.Windows.Media.SolidColorBrush Frozen(System.Windows.Media.SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Running state already probed by RefreshStatus() for the dashboard rows — reuse it
    /// instead of hitting the process table a second time in the same tick.</summary>
    private bool IsRunningCached(Services.ManagedService svc)
        => _dashRows.FirstOrDefault(r => ReferenceEquals(r.Service, svc))?.IsRunning ?? svc.IsRunning;

    /// <summary>Cheap 2-second tick for the Services page: dots + toggle text only.</summary>
    private void RefreshServicesQuick()
    {
        if (_svcCards is null) return;
        var ok = (System.Windows.Media.Brush)FindResource("OkBrush");
        foreach (var c in _svcCards)
        {
            if (c.Card is not null && c.Card.Visibility != Visibility.Visible) continue;
            var running = IsRunningCached(c.Service);
            var fill = running ? ok : IdleDotBrush;
            if (!ReferenceEquals(c.Dot.Fill, fill)) c.Dot.Fill = fill;
            var want = running ? "Stop" : "Start";
            // Only when it is a string — a ButtonSpinner ring may be showing while busy.
            if (c.ToggleBtn.Content is string s && s != want) c.ToggleBtn.Content = want;
        }
        if (CardPma.Visibility == Visibility.Visible)
        {
            var canOpen = Directory.Exists(Paths.PhpMyAdminDir) && IsRunningCached(Manager.Nginx);
            if (DbPmaOpenBtn.IsEnabled != canOpen) DbPmaOpenBtn.IsEnabled = canOpen;
        }
    }

    private void RefreshMailQuick()
    {
        var running = Manager.Mailpit.IsRunning;
        var fill = running ? (System.Windows.Media.Brush)FindResource("OkBrush") : IdleDotBrush;
        if (!ReferenceEquals(MailDot.Fill, fill)) MailDot.Fill = fill;
        var want = running ? "Stop" : "Start";
        if (MailToggleBtn.Content is string s && s != want) MailToggleBtn.Content = want;
        if (MailOpenBtn.IsEnabled != running) MailOpenBtn.IsEnabled = running;
    }

    private void RebuildServiceRows()
    {
        // Dashboard rows: everything except Mail (own page).
        _dashRows.Clear();
        foreach (var svc in Manager.AllServices)
            if (svc is not Services.MailpitService) _dashRows.Add(new ServiceRow(svc));
    }

    // ==================== Dashboard ====================

    /// <summary>One button: stops everything when anything runs, starts otherwise.</summary>
    private async void DashAll_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var spinner = ButtonSpinner.Start(btn);
        try
        {
            if (Manager.AllServices.Any(s => s.IsRunning))
            {
                DownloadStatus.Text = "Stopping all services…";
                await Manager.StopAllAsync();
                DownloadStatus.Text = "All services stopped.";
            }
            else
            {
                DownloadStatus.Text = "Starting enabled services…";
                await Manager.StartAutoStartServicesAsync((name, ex) =>
                    MessageBox.Show($"{name}: {ex.Message}", "LocalPanel", MessageBoxButton.OK, MessageBoxImage.Warning));
                DownloadStatus.Text = "Done.";
            }
        }
        finally { spinner.Dispose(); RefreshStatus(); }
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
            _mailpitCatalog = await VersionCatalog.GetMailpitAsync();
            _mariaCatalog = await VersionCatalog.GetMariaDbVersionsAsync();
            _pgCatalog = await VersionCatalog.GetPostgresVersionsAsync();
            _mongoCatalog = await VersionCatalog.GetMongoVersionsAsync();
            FillDbVersionCombos();


            // Background refresh: only replace the lists when the fetch succeeds.
            var php = await VersionCatalog.GetPhpVersionsOnlineAsync();
            var node = await VersionCatalog.GetNodeVersionsOnlineAsync();
            if (php is not null) _phpCatalog = php;
            if (node is not null) _nodeCatalog = node;
            RefreshVersionPages();
            RefreshServicesPage(fillFields: false); // update-availability needs the loaded catalogs
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
            // An update offer row REPRESENTS the installed old version — it shows
            // the old number, a small "new available" hint and the Update button.
            built.Add(new VersionRow(v, exact || updateFrom is not null, updateFrom,
                current is not null && (v.Version == current || updateFrom == current),
                exact ? detail(v.Version) : updateFrom is not null ? detail(updateFrom) : ""));
        }

        // Installed versions not represented by any catalog row (exact or update) still show up.
        foreach (var v in installed.Where(i =>
                     catalog.All(c => c.Version != i) && built.All(r => r.UpdateFrom != i)))
            built.Add(new VersionRow(new RemoteVersion(component, v, ""), true, null, v == current, detail(v)));

        foreach (var row in built.OrderByDescending(r => PadVersion(r.DisplayVersion), StringComparer.Ordinal))
        {
            row.ShowSettings = component == "php" && row.Installed;
            // Rows are rebuilt while an install may still be running — keep its Busy state.
            row.Busy = _installing.Contains(InstallKey(row.Remote));
            rows.Add(row);
        }
    }

    /// <summary>Installs in flight, keyed component|version — BuildVersionRows re-applies Busy from this.</summary>
    private readonly HashSet<string> _installing = new();
    private static string InstallKey(RemoteVersion r) => r.Component + "|" + r.Version;

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        var row = (VersionRow)((FrameworkElement)sender).DataContext;
        var key = InstallKey(row.Remote);
        if (!_installing.Add(key)) return; // already installing this exact version
        row.Busy = true;
        var spinner = ButtonSpinner.Start((Button)sender);
        try
        {
            if (row.UpdateFrom is not null && row.Remote.Component == "php")
            {
                // In-place update: settings + sites move to the new patch, old folder is removed.
                DownloadStatus.Text = $"Updating PHP {row.UpdateFrom} → {row.Version}…";
                await Manager.UpdatePhpAsync(row.Remote, row.UpdateFrom, InstallProgress(row.Remote));
                DownloadStatus.Text = $"PHP updated to {row.Version}.";
            }
            else
            {
                await InstallWithProgress(row.Remote);
                if (row.UpdateFrom is not null && row.Remote.Component == "node" &&
                    Manager.Config.CurrentNodeVersion == row.UpdateFrom)
                    await Task.Run(() => Manager.SetCurrentNode(row.Version));
            }
            RebuildServiceRows();
            RefreshPhpCombos();
            RefreshVersionPages();
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = (row.UpdateFrom is null ? "Install" : "Update") + " failed: " + Net.Friendly(ex);
        }
        finally
        {
            _installing.Remove(key);
            spinner.Dispose();
            row.Busy = false;
            // The list may have been rebuilt mid-install; release the row that replaced ours too.
            foreach (var r in _phpRows.Concat(_nodeRows))
                if (r.Busy && InstallKey(r.Remote) == key) r.Busy = false;
            DownloadProgress.Value = 0; // covers the UpdatePhpAsync branch, which bypasses InstallWithProgress
        }
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
        try
        {
            await Manager.InstallAsync(version, InstallProgress(version));
            DownloadStatus.Text = $"{version.Component} {version.Version} installed.";
        }
        finally { DownloadProgress.Value = 0; } // never leave a partial bar behind a failure message
    }

    // ==================== PHP settings ====================

    private void PhpSetVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PhpSetStatus.Text = ""; // a stale "Saved." must not carry over to another version
        if (PhpSetVersion.SelectedItem is not string v) return;
        var s = Manager.Config.Php.FirstOrDefault(p => p.Version == v);
        if (s is null) return;
        PhpSetMemory.Text = s.MemoryLimitMb.ToString();
        PhpSetUpload.Text = s.UploadMaxMb.ToString();
        PhpSetExec.Text = s.MaxExecutionSeconds.ToString();
        PhpSetInputVars.Text = s.MaxInputVars.ToString();
        PhpSetTz.Text = s.TimeZone;
        PhpSetWorkers.Text = s.Workers.ToString();
        PhpSetDisplayErr.IsChecked = s.DisplayErrors;

        var pool = Manager.PhpPools.FirstOrDefault(p => p.Settings.Version == v);
        var available = pool?.AvailableExtensions() ?? new List<string>();
        var enabled = pool?.EffectiveExtensions() ?? new List<string>();
        PhpExtList.ItemsSource = available
            .Select(a => new ExtChoice { Name = a, IsChecked = enabled.Contains(a) })
            .ToList();
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
        if (int.TryParse(PhpSetInputVars.Text, out var iv) && iv > 0) s.MaxInputVars = iv;
        if (PhpSetTz.Text.Trim().Length > 0) s.TimeZone = PhpSetTz.Text.Trim();
        // Capped at 16: workers take consecutive ports from FastCgiPort, and
        // NextFreeFastCgiPort hands each PHP version a block of exactly 16.
        if (int.TryParse(PhpSetWorkers.Text, out var wk) && wk > 0) s.Workers = Math.Min(wk, 16);
        s.DisplayErrors = PhpSetDisplayErr.IsChecked == true;
        if (PhpExtList.ItemsSource is List<ExtChoice> choices && choices.Count > 0)
            s.EnabledExtensions = choices.Where(c => c.IsChecked).Select(c => c.Name).ToList();
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


    // ==================== Sites ====================

    private void RefreshSitesList()
    {
        var q = SiteSearch?.Text.Trim() ?? "";
        var items = Manager.Config.Sites
            .Where(s => q.Length == 0 ||
                        s.Domain.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SitesListBox.ItemsSource = null;
        SitesListBox.ItemsSource = items;
    }

    /// <summary>App's startup chain calls this after the launch-time ApplySitesAsync (park import) finishes.</summary>
    public void RefreshSitesListPublic() => RefreshSitesList();

    private void SiteSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RefreshSitesList();

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

    /// <summary>True when the site is auto-parked: its root lives under www or a parked folder, so the
    /// folder is the source of truth and ImportParkedSites would simply re-import it after a delete.</summary>
    private bool IsAutoParked(Site s)
    {
        if (ServiceManager.ServesNoFiles(s) || string.IsNullOrWhiteSpace(s.RootPath)) return false;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.ResolvedRoot)); }
        catch { return false; }
        var roots = new[] { Paths.Www }.Concat(Manager.Config.ParkedPaths.Where(p => !string.IsNullOrWhiteSpace(p)));
        foreach (var r in roots)
        {
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)); }
            catch { continue; }
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void SitesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SitesStatus.Text = ""; // stale "saved" / "is live" must not show over another site's form
        // Delete only makes sense for proxy / hand-made sites; auto-parked ones come back from the folder.
        SiteDeleteBtn.Visibility = SitesListBox.SelectedItem is Site sel && !IsAutoParked(sel)
            ? Visibility.Visible : Visibility.Collapsed;
        if (SitesListBox.SelectedItem is not Site s) return;
        SiteFormTitle.Text = s.Domain;
        SiteDomain.Text = s.Domain;
        SiteRoot.Text = s.RootPath;
        SitePhpVersion.SelectedItem = s.PhpVersion ?? Manager.Config.DefaultPhpVersion;
        _editAliases.Clear();
        _editAliases.AddRange(s.Aliases);
        RefreshAliasChips();
        AliasInput.Text = "";
        SiteSsl.IsChecked = s.Ssl;
        LoadRoutes(s.Routes);
    }

    // ---------- routes ----------

    private readonly ObservableCollection<RouteRow> _editRoutes = new();

    private void LoadRoutes(List<SiteRoute> routes)
    {
        _editRoutes.Clear();
        foreach (var r in routes) _editRoutes.Add(RouteRow.From(r));
        if (_editRoutes.Count == 0) _editRoutes.Add(new RouteRow()); // a site always serves "/"
    }

    private void RouteAdd_Click(object sender, RoutedEventArgs e)
        => _editRoutes.Add(new RouteRow { Path = "/", Kind = "Proxy" });

    private void RouteRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RouteRow row) _editRoutes.Remove(row);
    }

    private void RouteBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RouteRow row) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for " + row.Path };
        if (dlg.ShowDialog(this) != true) return;
        row.Folder = dlg.FolderName;
        // Plain properties, so the grid has to be told to re-read the row.
        RouteList.Items.Refresh();
    }

    private void SiteNew_Click(object sender, RoutedEventArgs e)
    {
        SitesStatus.Text = "";
        SitesListBox.SelectedItem = null;
        SiteDeleteBtn.Visibility = Visibility.Collapsed;
        SiteFormTitle.Text = "New site";
        SiteDomain.Text = "";
        SiteRoot.Text = "";
        SitePhpVersion.SelectedItem = Manager.Config.DefaultPhpVersion;
        _editAliases.Clear();
        RefreshAliasChips();
        AliasInput.Text = "";
        SiteSsl.IsChecked = true;
        LoadRoutes(new List<SiteRoute>());
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
        site.PhpVersion = SitePhpVersion.SelectedItem as string;
        var aliases = _editAliases.ToList();
        if (!site.Aliases.SequenceEqual(aliases))
            SslManager.InvalidateCertificate(domain); // reissue with the new alias list
        site.Aliases = aliases;
        site.Ssl = SiteSsl.IsChecked == true;
        site.Routes = _editRoutes.Select(r => r.ToRoute()).ToList();
        if (site.Routes.Count == 0) site.Routes.Add(new SiteRoute()); // never leave a site with nothing to serve

        if (isNew) Manager.Config.Sites.Add(site);
        Manager.Config.Save();
        RefreshSitesList();
        SitesListBox.SelectedItem = site;
        _ = ApplyAllAsync($"{site.Domain} saved — applying…", $"{site.Domain} is live.");
    }

    private void SiteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SitesListBox.SelectedItem is not Site site) return;
        if (IsAutoParked(site))
        {
            MessageBox.Show(
                $"{site.Domain} is served from a parked folder — it would be re-imported automatically.\n\n" +
                $"Remove the folder {site.ResolvedRoot} instead, then LocalPanel drops the site itself.",
                "LocalPanel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                $"Delete site “{site.Domain}”?\n\nIts hosts entries and nginx config are removed; files on disk are untouched.",
                "LocalPanel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        Manager.Config.Sites.Remove(site);
        Manager.Config.Save();
        SslManager.InvalidateCertificate(site.Domain);
        RefreshSitesList();
        SiteNew_Click(sender, e);
        _ = ApplyAllAsync($"{site.Domain} deleted — applying…", $"{site.Domain} removed.");
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

    // ==================== Services (one page: Nginx + chosen services as cards) ====================

    /// <summary>Everything the page needs to know about one service card.</summary>
    private sealed record SvcCard(
        string Key, string Label, Services.ManagedService Service,
        Func<List<RemoteVersion>> Catalog,
        Border? Card, ComboBox? Ver, Button InstallBtn, Button ToggleBtn, System.Windows.Shapes.Ellipse Dot,
        TextBlock VerText, TextBox Port, CheckBox Auto);

    private List<SvcCard>? _svcCards;

    private List<SvcCard> SvcCards => _svcCards ??= new List<SvcCard>
    {
        new("nginx", "Nginx", Manager.Nginx, () => _nginxCatalog,
            null, null, DbNginxInstallBtn, DbNginxToggleBtn, DbNginxDot, DbNginxVerText,
            DbNginxPort, DbNginxAuto),
        new("mysql", "MySQL", Manager.MySql, () => _mysqlCatalog,
            CardMysql, DbMysqlVer, DbMysqlInstallBtn, DbMysqlToggleBtn, DbMysqlDot, DbMysqlVerText,
            DbMysqlPort, DbMysqlAuto),
        new("mariadb", "MariaDB", Manager.MariaDb, () => _mariaCatalog,
            CardMaria, DbMariaVer, DbMariaInstallBtn, DbMariaToggleBtn, DbMariaDot, DbMariaVerText,
            DbMariaPort, DbMariaAuto),
        new("postgres", "PostgreSQL", Manager.Postgres, () => _pgCatalog,
            CardPg, DbPgVer, DbPgInstallBtn, DbPgToggleBtn, DbPgDot, DbPgVerText,
            DbPgPort, DbPgAuto),
        new("mongodb", "MongoDB", Manager.Mongo, () => _mongoCatalog,
            CardMongo, DbMongoVer, DbMongoInstallBtn, DbMongoToggleBtn, DbMongoDot, DbMongoVerText,
            DbMongoPort, DbMongoAuto),
        new("redis", "Redis", Manager.Redis, () => _redisCatalog,
            CardRedis, DbRedisVer, DbRedisInstallBtn, DbRedisToggleBtn, DbRedisDot, DbRedisVerText,
            DbRedisPort, DbRedisAuto),
    };

    /// <summary>Optional (addable) services, in picker order — nginx is always shown.</summary>
    private static readonly (string Key, string Label, string Blurb)[] AddableServices =
    {
        ("mysql", "MySQL", "The classic — 8.4 LTS, 8.0, 5.7"),
        ("mariadb", "MariaDB", "MySQL-compatible, runs beside it on 3307"),
        ("postgres", "PostgreSQL", "17 / 16 / 15"),
        ("mongodb", "MongoDB", "Document DB — 8 / 7 / 6"),
        ("redis", "Redis", "Cache & queues — 8 / 7 / 6 / 5"),
        ("phpmyadmin", "phpMyAdmin", "Web UI for MySQL & MariaDB"),
    };

    private SvcCard CardOf(object sender)
    {
        var key = (string)((FrameworkElement)sender).Tag;
        return SvcCards.First(c => c.Key == key);
    }

    private bool IsSvcInstalled(string key) => key switch
    {
        "phpmyadmin" => Directory.Exists(Paths.PhpMyAdminDir),
        _ => SvcCards.First(c => c.Key == key).Service.IsInstalled,
    };

    /// <summary>Shown = installed OR explicitly added by the user.</summary>
    private bool IsSvcShown(string key)
        => IsSvcInstalled(key) || Manager.Config.EnabledServices.Contains(key);

    private void FillDbVersionCombos()
    {
        foreach (var c in SvcCards)
        {
            if (c.Ver is null) continue;
            var keep = c.Ver.SelectedItem as string;
            var list = c.Catalog().Select(v => v.Version).ToList();
            c.Ver.ItemsSource = list;
            c.Ver.SelectedItem = keep is not null && list.Contains(keep) ? keep : list.FirstOrDefault();
        }
    }

    private (int port, string user, string pass) DbCfg(string key) => key switch
    {
        "nginx" => (Manager.Config.Nginx.HttpPort, "", ""),
        "mysql" => (Manager.Config.MySql.Port, Manager.Config.MySql.User, Manager.Config.MySql.Password),
        "mariadb" => (Manager.Config.MariaDb.Port, Manager.Config.MariaDb.User, Manager.Config.MariaDb.Password),
        "postgres" => (Manager.Config.Postgres.Port, Manager.Config.Postgres.User, Manager.Config.Postgres.Password),
        "redis" => (Manager.Config.Redis.Port, "", ""),
        _ => (Manager.Config.Mongo.Port, "", ""),
    };

    private static string DbConnString(string key, int port, string user, string pass) => key switch
    {
        "mysql" or "mariadb" =>
            $"Host 127.0.0.1   Port {port}   User {user}   Password {(pass.Length == 0 ? "(empty)" : pass)}\r\n" +
            $"DB_CONNECTION=mysql  DB_HOST=127.0.0.1  DB_PORT={port}  DB_USERNAME={user}  DB_PASSWORD={pass}",
        "postgres" =>
            $"Host 127.0.0.1   Port {port}   User {user}   Password {pass}\r\n" +
            $"DB_CONNECTION=pgsql  DB_HOST=127.0.0.1  DB_PORT={port}  DB_USERNAME={user}  DB_PASSWORD={pass}\r\n" +
            $"postgresql://{user}:{pass}@127.0.0.1:{port}/postgres",
        "redis" =>
            $"Host 127.0.0.1   Port {port}   (no auth)\r\n" +
            $"REDIS_HOST=127.0.0.1  REDIS_PORT={port}  REDIS_CLIENT=phpredis",
        _ =>
            $"Host 127.0.0.1   Port {port}   (no auth)\r\n" +
            $"mongodb://127.0.0.1:{port}",
    };

    private void RefreshServicesPage(bool fillFields)
    {
        if (SvcCards is null) return;
        var ok = (System.Windows.Media.Brush)FindResource("OkBrush");
        var idle = IdleDotBrush;

        foreach (var c in SvcCards)
        {
            var shown = c.Key == "nginx" || IsSvcShown(c.Key);
            if (c.Card is not null) c.Card.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (!shown) continue;

            var installed = c.Service.IsInstalled;
            var running = c.Service.IsRunning;
            c.Dot.Fill = running ? ok : idle;
            c.ToggleBtn.Content = running ? "Stop" : "Start";
            c.ToggleBtn.IsEnabled = installed;
            c.Auto.IsChecked = c.Service.AutoStart;
            c.Auto.IsEnabled = installed;
            c.InstallBtn.Content = installed ? "Installed ✓" : "Install";
            c.InstallBtn.IsEnabled = !installed;
            var (port, user, pass) = DbCfg(c.Key);
            if (fillFields)
            {
                c.Port.Text = port.ToString();
                if (c.Key == "nginx") DbNginxHttps.Text = Manager.Config.Nginx.HttpsPort.ToString();
            }
            _ = EnrichSvcCardAsync(c, installed);
        }

        // phpMyAdmin card
        var pmaShown = IsSvcShown("phpmyadmin");
        CardPma.Visibility = pmaShown ? Visibility.Visible : Visibility.Collapsed;
        if (pmaShown)
        {
            var pma = Directory.Exists(Paths.PhpMyAdminDir);
            DbPmaInstallBtn.Content = pma ? "Installed ✓" : "Install";
            DbPmaInstallBtn.IsEnabled = !pma;
            DbPmaOpenBtn.IsEnabled = pma && Manager.Nginx.IsRunning;
            var pv = PmaVersion();
            DbPmaVerText.Text = pv.Length > 0 ? "v" + pv : "";
            DbPmaStatus.Text = !pma
                ? "Web UI for MySQL and MariaDB — install it and it appears at localhost/phpmyadmin."
                : Manager.Nginx.IsRunning
                    ? "Serving at localhost/phpmyadmin — logs in automatically with the MySQL / MariaDB credentials."
                    : "Installed — start Nginx to open it.";
        }

        // Add-service picker: only what is not shown yet
        AddServiceList.Children.Clear();
        foreach (var (key, label, blurb) in AddableServices)
        {
            if (IsSvcShown(key)) continue;
            var btn = new Button
            {
                Tag = key,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(14, 10, 14, 10),
                MinWidth = 200,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = label, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = blurb, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") },
                    }
                }
            };
            btn.Click += AddServicePick_Click;
            AddServiceList.Children.Add(btn);
        }
        var anyLeft = AddServiceList.Children.Count > 0;
        AddServiceBtn.IsEnabled = anyLeft;
        if (!anyLeft) AddServicePanel.Visibility = Visibility.Collapsed;
        ServicesHint.Text = anyLeft
            ? "Only the services you use are listed here. Nginx is always on."
            : "All available services are already listed. Nginx is always on.";
    }

    private async Task EnrichSvcCardAsync(SvcCard c, bool installed)
    {
        try
        {
            if (!installed) { c.VerText.Text = ""; return; }
            var v = await c.Service.GetInstalledVersionAsync();
            c.VerText.Text = v.Length > 0 ? "v" + v : "";
            var latest = c.Catalog().FirstOrDefault();
            if (v.Length > 0 && latest is not null &&
                string.CompareOrdinal(PadVersion(latest.Version), PadVersion(v)) > 0)
            {
                c.InstallBtn.Content = $"Update to {latest.Version}";
                c.InstallBtn.IsEnabled = true;
            }
        }
        catch { }
    }

    /// <summary>phpMyAdmin ships a RELEASE-DATE-x.y.z marker file in its root.</summary>
    private static string PmaVersion()
    {
        try
        {
            if (!Directory.Exists(Paths.PhpMyAdminDir)) return "";
            var f = Directory.GetFiles(Paths.PhpMyAdminDir, "RELEASE-DATE-*").FirstOrDefault();
            return f is null ? "" : Path.GetFileName(f)["RELEASE-DATE-".Length..];
        }
        catch { return ""; }
    }

    private void AddService_Click(object sender, RoutedEventArgs e)
        => AddServicePanel.Visibility = AddServicePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void AddServicePick_Click(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag;
        if (!Manager.Config.EnabledServices.Contains(key)) Manager.Config.EnabledServices.Add(key);
        Manager.Config.Save();
        AddServicePanel.Visibility = Visibility.Collapsed;
        RefreshServicesPage(fillFields: true);
    }

    private async void DbInstall_Click(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag;
        RemoteVersion? version;
        SvcCard? card = null;
        if (key == "phpmyadmin") version = _pmaCatalog.FirstOrDefault();
        else
        {
            card = CardOf(sender);
            var sel = card.Ver?.SelectedItem as string;
            version = card.Catalog().FirstOrDefault(v => v.Version == sel) ?? card.Catalog().FirstOrDefault();
        }
        if (version is null)
        {
            DownloadStatus.Text = "Catalog not loaded yet — try again in a moment.";
            return;
        }

        if (card is not null && card.Service.IsInstalled && card.Key is "mysql" or "mariadb" or "postgres" or "mongodb")
        {
            var cur = await card.Service.GetInstalledVersionAsync();
            if (cur.Length > 0 && cur != version.Version &&
                MessageBox.Show(
                    $"Switch {card.Label} {cur} → {version.Version}?\n\n" +
                    "Your databases are NOT touched — only the server binaries are replaced.\n" +
                    "An upgrade migrates data files one-way; going back later needs an export/import.",
                    "LocalPanel — " + card.Label, MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
                return;
        }

        var spinner = ButtonSpinner.Start((Button)sender);
        try
        {
            await InstallWithProgress(version);
            RebuildServiceRows();
        }
        catch (Exception ex) { DownloadStatus.Text = "Install failed: " + Net.Friendly(ex); }
        finally
        {
            spinner.Dispose();
            RefreshServicesPage(fillFields: false);
        }
    }

    private async void DbToggle_Click(object sender, RoutedEventArgs e)
    {
        var c = CardOf(sender);
        var spinner = ButtonSpinner.Start((Button)sender);
        try
        {
            if (c.Service.IsRunning) await c.Service.StopAsync();
            else await c.Service.StartAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, c.Label, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { spinner.Dispose(); }
        RefreshServicesPage(fillFields: false);
        RefreshStatus();
    }

    private async void SvcRestart_Click(object sender, RoutedEventArgs e)
    {
        var c = CardOf(sender);
        var spinner = ButtonSpinner.Start((Button)sender);
        try { await c.Service.RestartAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, c.Label, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { spinner.Dispose(); }
        RefreshServicesPage(fillFields: false);
        RefreshStatus();
    }

    private void SvcLog_Click(object sender, RoutedEventArgs e)
    {
        var c = CardOf(sender);
        if (!File.Exists(c.Service.LogFile)) { MessageBox.Show("No log yet for " + c.Label, "LocalPanel"); return; }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{c.Service.LogFile}\"") { UseShellExecute = true });
    }

    private void DbAuto_Click(object sender, RoutedEventArgs e)
    {
        var c = CardOf(sender);
        c.Service.AutoStart = c.Auto.IsChecked == true;
    }

    /// <summary>Port(s) only — credentials live in the Connection… dialog.</summary>
    private async void DbSave_Click(object sender, RoutedEventArgs e)
    {
        var c = CardOf(sender);
        if (!int.TryParse(c.Port.Text, out var port) || port <= 0) { DownloadStatus.Text = "Invalid port."; return; }
        var (oldPort, _, _) = DbCfg(c.Key);
        var cfg = Manager.Config;
        var changed = port != oldPort;
        switch (c.Key)
        {
            case "nginx":
                cfg.Nginx.HttpPort = port;
                if (int.TryParse(DbNginxHttps.Text, out var https) && https > 0)
                {
                    changed |= https != cfg.Nginx.HttpsPort;
                    cfg.Nginx.HttpsPort = https;
                }
                break;
            case "mysql": cfg.MySql.Port = port; break;
            case "mariadb": cfg.MariaDb.Port = port; break;
            case "postgres": cfg.Postgres.Port = port; break;
            case "mongodb": cfg.Mongo.Port = port; break;
            case "redis": cfg.Redis.Port = port; break;
        }
        cfg.Save();
        Manager.ConfigurePhpMyAdmin();

        var spinner = ButtonSpinner.Start((Button)sender);
        try
        {
            if (changed && c.Service.IsRunning) await c.Service.RestartAsync();
            if (c.Key == "nginx") await Manager.ApplySitesAsync();
            DownloadStatus.Text = $"{c.Label} port saved" + (changed && c.Service.IsRunning ? " and applied." : ".");
        }
        catch (Exception ex) { DownloadStatus.Text = "Saved, but apply failed: " + ex.Message; }
        finally { spinner.Dispose(); }
        RefreshServicesPage(fillFields: false);
        RefreshStatus();
    }

    /// <summary>Connection… button: popup with credentials (editable) and copy-ready connection text.</summary>
    private void DbConnection_Click(object sender, RoutedEventArgs e)
    {
        var c = CardOf(sender);
        var hasCreds = c.Key is "mysql" or "mariadb" or "postgres";
        var (port, user, pass) = DbCfg(c.Key);
        var dlg = new ConnectionDialog(c.Label + " connection", user, pass,
            DbConnString(c.Key, port, user, pass), hasCreds)
        {
            Owner = this,
            BuildConnection = () =>
            {
                var (p, u, pw) = DbCfg(c.Key);
                return DbConnString(c.Key, p, u, pw);
            },
            OnSave = async (newUser, newPass) =>
            {
                var (_, oldUser, oldPass) = DbCfg(c.Key);
                if (newUser == oldUser && newPass == oldPass) return null;
                // Credentials are changed ON the running server; persisting them while it is stopped
                // would leave config.json out of sync with what the server actually accepts.
                if (!c.Service.IsRunning)
                    return "Start the server first — credentials are changed on the running server.";
                var cfg = Manager.Config;
                void Set(string u, string p)
                {
                    switch (c.Key)
                    {
                        case "mysql": cfg.MySql.User = u; cfg.MySql.Password = p; break;
                        case "mariadb": cfg.MariaDb.User = u; cfg.MariaDb.Password = p; break;
                        case "postgres": cfg.Postgres.User = u; cfg.Postgres.Password = p; break;
                    }
                }
                // Apply first (the services read the NEW values from Config, authenticating with the old ones),
                // persist only when the server accepted them; otherwise roll the in-memory values back.
                Set(newUser, newPass);
                string? err;
                try
                {
                    err = c.Service switch
                    {
                        Services.MySqlService m => await m.ApplyCredentialsAsync(oldUser, oldPass),
                        Services.MariaDbService m => await m.ApplyCredentialsAsync(oldUser, oldPass),
                        Services.PostgresService p => await p.ApplyCredentialsAsync(oldUser, oldPass),
                        _ => null,
                    };
                }
                catch (Exception ex) { err = ex.Message; }
                if (err is not null)
                {
                    Set(oldUser, oldPass);
                    return err;
                }
                cfg.Save();
                Manager.ConfigurePhpMyAdmin();
                return null;
            },
        };
        dlg.ShowDialog();
    }

    private void DbPmaOpen_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("http://localhost/phpmyadmin/") { UseShellExecute = true });

    // ==================== Mail (everything Mailpit lives on this page) ====================

    private void RefreshMailPage(bool fillPorts)
    {
        var svc = Manager.Mailpit;
        var installed = svc.IsInstalled;
        var running = svc.IsRunning;
        var cfg = Manager.Config.Mailpit;

        MailDot.Fill = running ? (System.Windows.Media.Brush)FindResource("OkBrush") : IdleDotBrush;
        MailToggleBtn.Content = running ? "Stop" : "Start";
        MailToggleBtn.IsEnabled = installed;
        MailOpenBtn.IsEnabled = running;
        MailInstallBtn.Content = installed ? "Installed ✓" : "Install";
        MailInstallBtn.IsEnabled = !installed;
        MailStatus.Text = installed
            ? $"SMTP: smtp://127.0.0.1:{cfg.SmtpPort}   •   Inbox: http://127.0.0.1:{cfg.UiPort}"
            : "Not installed yet — one click and every local email gets caught here.";
        if (fillPorts)
        {
            MailSmtpBox.Text = cfg.SmtpPort.ToString();
            MailUiBox.Text = cfg.UiPort.ToString();
        }
        MailEnvBox.Text = $"MAIL_HOST=127.0.0.1\r\nMAIL_PORT={cfg.SmtpPort}";
        _ = EnrichMailVersionAsync(installed);
    }

    private async Task EnrichMailVersionAsync(bool installed)
    {
        try
        {
            if (!installed) { MailVerText.Text = ""; return; }
            var v = await Manager.Mailpit.GetInstalledVersionAsync();
            MailVerText.Text = v.Length > 0 ? "v" + v : "";
            var latest = _mailpitCatalog?.Version;
            if (v.Length > 0 && latest is not null && latest != "latest" &&
                string.CompareOrdinal(PadVersion(latest), PadVersion(v)) > 0)
            {
                MailInstallBtn.Content = $"Update to {latest}";
                MailInstallBtn.IsEnabled = true;
            }
        }
        catch { }
    }

    private async void MailInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_mailpitCatalog is null)
        {
            MailStatus.Text = "Catalog not loaded yet — try again in a moment.";
            return;
        }
        var spinner = ButtonSpinner.Start((Button)sender);
        try
        {
            await InstallWithProgress(_mailpitCatalog);
            MailStatus.Text = "Mailpit installed.";
        }
        catch (Exception ex) { MailStatus.Text = "Install failed: " + Net.Friendly(ex); }
        finally
        {
            spinner.Dispose();
            RefreshMailPage(fillPorts: false);
        }
    }

    private async void MailToggle_Click(object sender, RoutedEventArgs e)
    {
        var svc = Manager.Mailpit;
        try
        {
            if (svc.IsRunning) await svc.StopAsync();
            else await svc.StartAsync();
        }
        catch (Exception ex) { MailStatus.Text = ex.Message; }
        RefreshMailPage(fillPorts: false);
    }

    private void MailOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (!Manager.Mailpit.IsInstalled) return;
        Process.Start(new ProcessStartInfo(Manager.Mailpit.UiUrl) { UseShellExecute = true });
    }

    private async void MailPortsSave_Click(object sender, RoutedEventArgs e)
    {
        var cfg = Manager.Config.Mailpit;
        if (int.TryParse(MailSmtpBox.Text, out var smtp) && smtp > 0) cfg.SmtpPort = smtp;
        if (int.TryParse(MailUiBox.Text, out var ui) && ui > 0) cfg.UiPort = ui;
        Manager.Config.Save();
        try
        {
            // PHP mail() points at the SMTP port — regenerate inis and bounce pools.
            foreach (var pool in Manager.PhpPools.Where(p => p.IsInstalled))
            {
                pool.WriteIni();
                if (pool.IsRunning) await pool.RestartAsync();
            }
            if (Manager.Mailpit.IsRunning) await Manager.Mailpit.RestartAsync();
            MailStatus.Text = "Ports saved and applied.";
        }
        catch (Exception ex) { MailStatus.Text = "Saved, but apply failed: " + ex.Message; }
        RefreshMailPage(fillPorts: false);
    }

    // ==================== PM2 ====================

    private async void Pm2Refresh_Click(object sender, RoutedEventArgs e) => await Pm2RefreshAsync();

    private async Task Pm2RefreshAsync()
    {
        // Locate() walks every PATH entry with File.Exists — dead network shares stall for seconds.
        var found = await Task.Run(Manager.Pm2.Locate);
        Pm2InstallBtn.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
        Pm2InstallBtn.Content = "Install PM2";
        if (!found)
        {
            Pm2StatusText.Text = Directory.Exists(Paths.NodeCurrent)
                ? "PM2 is not installed yet — one click and it rides on your current Node."
                : "PM2 needs Node — install a Node version first, then come back here.";
            Pm2List.ItemsSource = null;
            Pm2Empty.Visibility = Visibility.Collapsed;
            return;
        }

        var apps = await Manager.Pm2.ListAsync();
        Pm2List.ItemsSource = apps;
        Pm2Empty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var ver = await Manager.Pm2.VersionAsync();
        Pm2StatusText.Text = (ver.Length > 0 ? $"PM2 v{ver}" : "PM2") +
                             $"  ·  {apps.Count(a => a.IsOnline)} of {apps.Count} apps running";
        _ = Pm2UpdateCheckAsync(ver);
    }

    /// <summary>`npm view pm2 version` result for this session — the registry is asked once, not on every page visit.</summary>
    private string? _pm2Latest;
    private bool _pm2LatestChecked;

    private async Task Pm2UpdateCheckAsync(string installedVersion)
    {
        try
        {
            if (installedVersion.Length == 0) return;
            if (!_pm2LatestChecked)
            {
                _pm2LatestChecked = true;
                _pm2Latest = await Manager.Pm2.NpmLatestAsync();
            }
            var latest = _pm2Latest;
            if (latest is null) return;
            if (string.CompareOrdinal(PadVersion(latest), PadVersion(installedVersion)) > 0)
            {
                Pm2InstallBtn.Content = $"Update to {latest}";
                Pm2InstallBtn.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }


    private async void Pm2Install_Click(object sender, RoutedEventArgs e)
    {
        var npm = Path.Combine(Paths.NodeCurrent, "npm.cmd");
        if (!File.Exists(npm))
        {
            Pm2StatusText.Text = "Install a Node version first (Node page) — PM2 is an npm package.";
            return;
        }
        var spinner = ButtonSpinner.Start((Button)sender);
        Pm2StatusText.Text = "Installing PM2 (npm install -g pm2)…";
        try
        {
            var env = new Dictionary<string, string>
            {
                ["PATH"] = Paths.NodeCurrent + ";" + Environment.GetEnvironmentVariable("PATH"),
            };
            var (code, so, se) = await ProcessRunner.RunAsync(npm, "install -g pm2",
                Paths.NodeCurrent, env, timeoutMs: 300_000);
            if (code != 0)
            {
                var output = se + so;
                Pm2StatusText.Text = output.Contains("ENOTFOUND") || output.Contains("ETIMEDOUT") ||
                                     output.Contains("network", StringComparison.OrdinalIgnoreCase)
                    ? "No internet connection — check your connection and try again."
                    : "npm failed: " + output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim();
                return;
            }
            Pm2StatusText.Text = "PM2 installed.";
            await Pm2RefreshAsync();
        }
        catch (Exception ex) { Pm2StatusText.Text = "Install failed: " + Net.Friendly(ex); }
        finally { spinner.Dispose(); }
    }

    private static Pm2Process RowApp(object sender) => (Pm2Process)((FrameworkElement)sender).DataContext;

    private async void Pm2RowToggle_Click(object sender, RoutedEventArgs e)
    {
        var app = RowApp(sender);
        var spinner = ButtonSpinner.Start((Button)sender);
        try
        {
            if (app.IsOnline) await Manager.Pm2.StopAsync(app.Id);
            else await Manager.Pm2.StartAppAsync(app.Id);
        }
        finally { spinner.Dispose(); }
        await Pm2RefreshAsync();
    }

    private async void Pm2RowRestart_Click(object sender, RoutedEventArgs e)
    {
        var app = RowApp(sender);
        var spinner = ButtonSpinner.Start((Button)sender);
        try { await Manager.Pm2.RestartAsync(app.Id); }
        finally { spinner.Dispose(); }
        await Pm2RefreshAsync();
    }

    private async void Pm2RowLogs_Click(object sender, RoutedEventArgs e)
    {
        var app = RowApp(sender);
        Pm2LogBox.Text = await Manager.Pm2.LogsAsync(app.Id);
    }

    private async void Pm2RowDelete_Click(object sender, RoutedEventArgs e)
    {
        var app = RowApp(sender);
        if (MessageBox.Show($"Remove “{app.Name}” from PM2? (the project files are untouched)",
                "LocalPanel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Manager.Pm2.DeleteAsync(app.Id);
        await Pm2RefreshAsync();
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

    /// <summary>Toggles persist the instant they are clicked — no Save button needed.</summary>
    private void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        var c = Manager.Config;
        c.AutoStartServicesOnLaunch = SetAutoLaunch.IsChecked == true;
        c.StartMinimizedToTray = SetStartMinimized.IsChecked == true;
        c.CheckUpdatesOnStart = SetAutoUpdate.IsChecked == true;
        c.Save();

        if (ReferenceEquals(sender, SetLaunchAtLogin))
        {
            try
            {
                StartupManager.SetEnabled(SetLaunchAtLogin.IsChecked == true);
                // Read back so the box always reflects the real registry state.
                SetLaunchAtLogin.IsChecked = StartupManager.IsEnabled;
                SettingsStatus.Text = SetLaunchAtLogin.IsChecked == true
                    ? "LocalPanel will launch at Windows login."
                    : "Removed from Windows startup.";
            }
            catch (Exception ex)
            {
                SetLaunchAtLogin.IsChecked = StartupManager.IsEnabled;
                MessageBox.Show("Could not update the Windows startup entry: " + ex.Message,
                    "LocalPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            SettingsStatus.Text = "Saved.";
        }
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
