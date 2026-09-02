using System.Diagnostics;
using System.IO;
using LocalPanel.Core;
using LocalPanel.Services;

namespace LocalPanel.App;

public static class Program
{
    private static ServiceManager _manager = null!;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _manager = new ServiceManager();

        // Self-register the CLI shims the first time this build runs, so "localpanel" and
        // the short "lpanel" work from any directory instead of only via ./dist/localpanel.
        // Cheap existence check — the full rewrite only happens once.
        try
        {
            if (!File.Exists(Path.Combine(Paths.ShimsDir, "lpanel")))
            {
                ShimManager.Apply(_manager.Config);
                ShimManager.EnsureUserPath();
            }
        }
        catch { /* shims are a convenience; never block a command over them */ }

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            ShowBanner();
            ShowHelp();
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "status" or "ps":
                    ShowBanner();
                    await ShowStatusAsync();
                    return 0;

                case "start":
                    return await HandleStartAsync(args.Skip(1).ToArray());

                case "stop":
                    return await HandleStopAsync(args.Skip(1).ToArray());

                case "restart":
                    return await HandleRestartAsync(args.Skip(1).ToArray());

                case "node" or "nvm":
                    return await HandleNodeAsync(args.Skip(1).ToArray());

                case "php":
                    return await HandlePhpAsync(args.Skip(1).ToArray());

                case "sites":
                    return await HandleSitesAsync(args.Skip(1).ToArray());

                case "update" or "self-update" or "--update":
                    return await HandleUpdateAsync();

                case "gui" or "app" or "window":
                    var guiExe = Path.Combine(Paths.Root, "dist", Platform.Exe("localpanel-gui"));
                    if (!File.Exists(guiExe))
                        guiExe = Path.Combine(Paths.Root, "src", "LocalPanel.Desktop", "bin", "Release", "net8.0", Platform.Exe("localpanel-gui"));
                    if (!File.Exists(guiExe))
                        guiExe = Path.Combine(Paths.Root, "src", "LocalPanel.Desktop", "bin", "Debug", "net8.0", Platform.Exe("localpanel-gui"));
                    if (File.Exists(guiExe))
                    {
                        Process.Start(new ProcessStartInfo { FileName = guiExe, UseShellExecute = true });
                        Console.WriteLine("Launching LocalPanel Desktop GUI...");
                        return 0;
                    }
                    Console.WriteLine("Desktop GUI not found. Run ./scripts/build.sh first.");
                    return 1;

                case "version" or "--version" or "-v":
                    Console.WriteLine("LocalPanel v1.0.0 (Linux & Windows Cross-Platform)");
                    return 0;

                default:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Unknown command: {cmd}");
                    Console.ResetColor();
                    ShowHelp();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[Error] {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static void ShowBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  _                    _ _____                  _ 
 | |    ___   ___ __ _| |  __ \ __ _ _ __   ___| |
 | |   / _ \ / __/ _` | | |__) / _` | '_ \ / _ \ |
 | |__| (_) | (_| (_| | |  ___/ (_| | | | |  __/ |
 |_____\___/ \___\__,_|_|_|    \__,_|_| |_|\___|_|
        ");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var osName = Platform.IsWindows ? "Windows" : "Linux";
        Console.WriteLine($" Portable Development Stack · {osName} · Root: {Paths.Root}");
        Console.WriteLine(new string('─', 65));
        Console.ResetColor();
    }

    private static void ShowHelp()
    {
        Console.WriteLine("\nUsage: localpanel <command> [options]\n");
        Console.WriteLine("Commands:");
        Console.WriteLine("  status                 Show status of all services, PHP pools, and Node/NVM");
        Console.WriteLine("  start [service]        Start all auto-start services or a specific service");
        Console.WriteLine("  stop [service]         Stop all services or a specific service");
        Console.WriteLine("  restart [service]      Restart all services or a specific service");
        Console.WriteLine("  node | nvm <command>   Manage Node.js versions (list, use, install)");
        Console.WriteLine("  php <command>          Manage PHP versions (list, use, install)");
        Console.WriteLine("  sites [sync|list]      Manage parked sites, SSL certs, and hosts");
        Console.WriteLine("  update                 Update LocalPanel to the latest release");
        Console.WriteLine("  version                Show version information\n");
    }

    private static async Task ShowStatusAsync()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Services Status:");
        Console.ResetColor();

        foreach (var svc in _manager.AllServices)
        {
            var isRunning = svc.IsRunning;
            var isInstalled = svc.IsInstalled;

            Console.Write($"  • {svc.DisplayName,-16} ");
            if (!isInstalled)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("[Not Installed]");
            }
            else if (isRunning)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("[Running]");
                Console.ResetColor();
                if (!string.IsNullOrEmpty(svc.PortInfo))
                    Console.Write($"  (Port: {svc.PortInfo})");
                Console.WriteLine();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Stopped]");
            }
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\nNode / NVM Version Manager:");
        Console.ResetColor();
        var nodeVersions = Paths.VersionDirs(Paths.NodeDir).Select(Path.GetFileName).ToList();
        if (nodeVersions.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  No Node versions installed yet. Run 'localpanel node install 22' to install.");
            Console.ResetColor();
        }
        else
        {
            foreach (var v in nodeVersions)
            {
                var isActive = string.Equals(v, _manager.Config.CurrentNodeVersion, StringComparison.OrdinalIgnoreCase);
                if (isActive)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  * Node {v} (Active Global -> node, npm, npx)");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                    var major = v?.Split('.')[0];
                    Console.WriteLine($"    Node {v} (available via node{major}, npm{major})");
                }
                Console.ResetColor();
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\nParked Sites:");
        Console.ResetColor();
        if (_manager.Config.Sites.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  No sites configured. Place projects in 'www/' to auto-park.");
            Console.ResetColor();
        }
        else
        {
            foreach (var site in _manager.Config.Sites)
            {
                var scheme = NginxConfigGenerator.HasCert(site) ? "https" : "http";
                Console.WriteLine($"  • {site.Domain,-20} -> {scheme}://{site.Domain} (PHP: {site.PhpVersion ?? "default"})");
            }
        }
        Console.WriteLine();
    }

    private static async Task<int> HandleStartAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Starting enabled LocalPanel services...");
            await _manager.StartAutoStartServicesAsync((name, err) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [!] Failed to start {name}: {err.Message}");
                Console.ResetColor();
            });
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Started.");
            Console.ResetColor();
            return 0;
        }

        var target = args[0].ToLowerInvariant();
        var svc = _manager.AllServices.FirstOrDefault(s => s.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (svc is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Service '{target}' not found.");
            Console.ResetColor();
            return 1;
        }

        Console.Write($"Starting {svc.DisplayName}... ");
        await svc.StartAsync();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Done.");
        Console.ResetColor();
        return 0;
    }

    private static async Task<int> HandleStopAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Stopping all LocalPanel services...");
            await _manager.StopAllAsync();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Stopped.");
            Console.ResetColor();
            return 0;
        }

        var target = args[0].ToLowerInvariant();
        var svc = _manager.AllServices.FirstOrDefault(s => s.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (svc is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Service '{target}' not found.");
            Console.ResetColor();
            return 1;
        }

        Console.Write($"Stopping {svc.DisplayName}... ");
        await svc.StopAsync();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Stopped.");
        Console.ResetColor();
        return 0;
    }

    private static async Task<int> HandleRestartAsync(string[] args)
    {
        await HandleStopAsync(args);
        await Task.Delay(500);
        return await HandleStartAsync(args);
    }

    private static async Task<int> HandleNodeAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "list" or "ls")
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Installed Node (NVM) Versions:");
            Console.ResetColor();
            var installed = Paths.VersionDirs(Paths.NodeDir).Select(Path.GetFileName).ToList();
            if (installed.Count == 0)
            {
                Console.WriteLine("  No versions installed yet.");
            }
            else
            {
                foreach (var v in installed)
                {
                    var isCurrent = string.Equals(v, _manager.Config.CurrentNodeVersion, StringComparison.OrdinalIgnoreCase);
                    if (isCurrent)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  * {v} (Active Global: node, npm, npx)");
                    }
                    else
                    {
                        var major = v?.Split('.')[0];
                        Console.WriteLine($"    {v} (Command: node{major}, npm{major})");
                    }
                    Console.ResetColor();
                }
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nAvailable LTS Versions for Download:");
            Console.ResetColor();
            var available = await VersionCatalog.GetNodeVersionsOnlineAsync() ?? VersionCatalog.GetNodeVersionsOffline();
            foreach (var r in available)
            {
                var isInst = installed.Contains(r.Version);
                Console.WriteLine($"    v{r.Version,-12} {(isInst ? "[Installed]" : "")}");
            }
            return 0;
        }

        var sub = args[0].ToLowerInvariant();
        if (sub == "use")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: localpanel node use <version> (e.g. 20, 22.23.2)");
                return 1;
            }
            var target = args[1];
            var installed = Paths.VersionDirs(Paths.NodeDir).Select(Path.GetFileName).ToList();
            var match = installed.FirstOrDefault(v => v != null && v.StartsWith(target, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Node version matching '{target}' is not installed.");
                Console.ResetColor();
                return 1;
            }
            _manager.SetCurrentNode(match);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Switched active Node version to {match} (node, npm, npx now point to it).");
            Console.ResetColor();
            return 0;
        }

        if (sub == "install")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: localpanel node install <version> (e.g. 20, 22, 24)");
                return 1;
            }
            var target = args[1].TrimStart('v');
            var available = await VersionCatalog.GetNodeVersionsOnlineAsync() ?? VersionCatalog.GetNodeVersionsOffline();
            var remote = available.FirstOrDefault(r => r.Version.StartsWith(target, StringComparison.OrdinalIgnoreCase));
            if (remote is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"No downloadable Node version found matching '{target}'.");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine($"Downloading and installing Node v{remote.Version}...");
            var progress = new Progress<(long done, long total)>(p =>
            {
                if (p.total > 0)
                    Console.Write($"\rDownloading: {p.done * 100 / p.total}% ({p.done / 1048576}MB / {p.total / 1048576}MB)");
            });

            await _manager.InstallAsync(remote, progress);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nSuccessfully installed Node {remote.Version}!");
            Console.WriteLine($"Use 'localpanel node use {remote.Version}' or run 'node{remote.Version.Split('.')[0]}' directly.");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine("Unknown node command. Use 'list', 'use', or 'install'.");
        return 1;
    }

    private static async Task<int> HandlePhpAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "list" or "ls")
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Installed PHP Versions:");
            Console.ResetColor();
            var installed = _manager.PhpPools;
            if (installed.Count == 0)
            {
                Console.WriteLine("  No PHP versions installed yet.");
            }
            else
            {
                foreach (var p in installed)
                {
                    var isDefault = string.Equals(p.Settings.Version, _manager.Config.DefaultPhpVersion);
                    Console.WriteLine($"  {(isDefault ? "*" : " ")} PHP {p.Settings.Version,-10} {(isDefault ? "(Default)" : "")} - Status: {(p.IsRunning ? "Running" : "Stopped")}");
                }
            }
            return 0;
        }

        var sub = args[0].ToLowerInvariant();
        if (sub == "use")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: localpanel php use <version>");
                return 1;
            }
            var target = args[1];
            var match = _manager.PhpPools.FirstOrDefault(p => p.Settings.Version.StartsWith(target));
            if (match is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"PHP version '{target}' is not installed.");
                Console.ResetColor();
                return 1;
            }
            _manager.SetCurrentPhp(match.Settings.Version);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Default PHP version set to {match.Settings.Version}.");
            Console.ResetColor();
            return 0;
        }

        return 0;
    }

    private static async Task<int> HandleSitesAsync(string[] args)
    {
        Console.WriteLine("Syncing parked sites, SSL certs, and hosts file...");
        await _manager.ApplySitesAsync();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Sites synced successfully.");
        Console.ResetColor();
        return 0;
    }

    private static async Task<int> HandleUpdateAsync()
    {
        Console.WriteLine("Checking GitHub Releases for updates...");
        var newTag = await AppUpdater.CheckAndDownloadAsync();
        if (newTag is null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("You are running the latest version of LocalPanel!");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Downloaded version {newTag}. Applying update...");
        Console.ResetColor();
        AppUpdater.ApplyDownloadedUpdate();
        return 0;
    }
}
