using System.IO;
using LocalPanel.Services;

namespace LocalPanel.Core;

/// <summary>
/// Central coordinator: owns the config, the list of managed services,
/// component installation, and applying site changes end-to-end.
/// </summary>
public class ServiceManager
{
    public AppConfig Config { get; }
    public NginxService Nginx { get; }
    public MySqlService MySql { get; }
    public RedisService Redis { get; }
    public MailpitService Mailpit { get; }
    public MariaDbService MariaDb { get; }
    public PostgresService Postgres { get; }
    public MongoService Mongo { get; }
    public List<PhpService> PhpPools { get; } = new();
    public Pm2Manager Pm2 { get; } = new();

    public ServiceManager()
    {
        Config = AppConfig.Load();
        Nginx = new NginxService(Config);
        MySql = new MySqlService(Config);
        Redis = new RedisService(Config);
        Mailpit = new MailpitService(Config);
        MariaDb = new MariaDbService(Config);
        Postgres = new PostgresService(Config);
        Mongo = new MongoService(Config);
        SyncPhpPools();
    }

    public IEnumerable<ManagedService> AllServices
    {
        get
        {
            yield return Nginx;
            yield return MySql;
            yield return Redis;
            yield return Mailpit;
            yield return MariaDb;
            yield return Postgres;
            yield return Mongo;
            foreach (var p in PhpPools) yield return p;
        }
    }

    private const string OldDirMarker = ".old";

    private void SyncPhpPools()
    {
        PhpPools.Clear();
        Config.Php.RemoveAll(p => !Directory.Exists(Path.Combine(Paths.PhpDir, p.Version)));
        foreach (var dir in Paths.VersionDirs(Paths.PhpDir))
        {
            var version = Path.GetFileName(dir);
            if (version.Contains(OldDirMarker, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
                continue;
            }
            if (Config.Php.All(p => p.Version != version))
                Config.Php.Add(new PhpVersionSettings { Version = version, FastCgiPort = NextFreeFastCgiPort() });
        }
        if (Config.DefaultPhpVersion is null ||
            Config.Php.All(p => p.Version != Config.DefaultPhpVersion))
            Config.DefaultPhpVersion = Config.Php.FirstOrDefault()?.Version;
        Config.Save();
        foreach (var p in Config.Php)
            PhpPools.Add(new PhpService(Config, p));
    }

    private int NextFreeFastCgiPort()
    {
        var used = Config.Php.Select(p => p.FastCgiPort).ToHashSet();
        var port = 9000;
        while (used.Any(u => port >= u && port < u + 16)) port += 16;
        return port;
    }

    // ---------------- installation ----------------

    public async Task InstallAsync(RemoteVersion version, IProgress<(long, long)>? progress = null)
    {
        switch (version.Component)
        {
            case "php":
                await Downloader.DownloadAndExtractAsync(version.Url,
                    Path.Combine(Paths.PhpDir, version.Version), progress);
                SyncPhpPools();
                await ApplyShimsAsync();
                try { await InstallPhpRedisExtensionAsync(version.Version, progress); } catch { }
                try { await EnsureComposerAsync(); } catch { }
                break;

            case "node":
                await Downloader.DownloadAndExtractAsync(version.Url,
                    Path.Combine(Paths.NodeDir, version.Version), progress);
                Config.CurrentNodeVersion ??= version.Version;
                Config.Save();
                await ApplyShimsAsync();
                break;

            case "composer":
                await Downloader.DownloadFileAsync(version.Url, Paths.ComposerPhar, progress);
                await ApplyShimsAsync();
                break;

            case "mailpit":
            {
                var mailpitWasRunning = Mailpit.IsRunning;
                try
                {
                    await Downloader.DownloadAndExtractAsync(version.Url, Paths.MailpitDir, progress,
                        beforeSwap: mailpitWasRunning ? () => Mailpit.StopAsync() : null);
                    foreach (var pool in PhpPools.Where(p => p.IsInstalled))
                    {
                        pool.WriteIni();
                        if (pool.IsRunning) await pool.RestartAsync();
                    }
                }
                finally
                {
                    if (mailpitWasRunning && Mailpit.IsInstalled && !Mailpit.IsRunning) await Mailpit.StartAsync();
                }
                break;
            }

            case "nginx":
            {
                var nginxWasRunning = Nginx.IsRunning;
                try
                {
                    await Downloader.DownloadAndExtractAsync(version.Url, Paths.NginxDir, progress,
                        beforeSwap: nginxWasRunning ? () => Nginx.StopAsync() : null);
                    await ApplySitesAsync();
                }
                finally
                {
                    if (nginxWasRunning && Nginx.IsInstalled && !Nginx.IsRunning) await Nginx.StartAsync();
                }
                break;
            }

            case "mysql":
            {
                var installedMy = MySql.IsInstalled ? await MySql.GetInstalledVersionAsync() : "";
                if (installedMy.Length > 0 && MySql.IsInitialized &&
                    string.CompareOrdinal(PadV(version.Version), PadV(installedMy)) < 0)
                    throw new InvalidOperationException(
                        $"Downgrading MySQL ({installedMy} → {version.Version}) would corrupt the existing databases.\n" +
                        "Export them first (phpMyAdmin), remove data/mysql, then install the older version.");
                var myWasRunning = MySql.IsRunning;
                try
                {
                    await Downloader.DownloadAndExtractAsync(version.Url, Paths.MySqlDir, progress,
                        beforeSwap: myWasRunning ? () => MySql.StopAsync() : null);
                }
                finally
                {
                    if (myWasRunning && MySql.IsInstalled && !MySql.IsRunning) await MySql.StartAsync();
                }
                break;
            }

            case "redis":
            {
                var redisWasRunning = Redis.IsRunning;
                try
                {
                    await Downloader.DownloadAndExtractAsync(version.Url, Paths.RedisDir, progress,
                        beforeSwap: redisWasRunning ? () => Redis.StopAsync() : null);
                }
                finally
                {
                    if (redisWasRunning && Redis.IsInstalled && !Redis.IsRunning) await Redis.StartAsync();
                }
                break;
            }

            case "mariadb":
                await SwapServerBinariesAsync(MariaDb, Paths.MariaDbDir, version, progress,
                    MariaDb.IsInitialized, "MariaDB", "data/mariadb");
                ConfigurePhpMyAdmin();
                break;

            case "postgres":
                await SwapServerBinariesAsync(Postgres, Paths.PostgresDir, version, progress,
                    Postgres.IsInitialized, "PostgreSQL", "data/postgres");
                break;

            case "mongodb":
                await SwapServerBinariesAsync(Mongo, Paths.MongoDir, version, progress,
                    Directory.Exists(Paths.MongoData), "MongoDB", "data/mongodb");
                break;

            case "phpmyadmin":
                await Downloader.DownloadAndExtractAsync(version.Url, Paths.PhpMyAdminDir, progress);
                ConfigurePhpMyAdmin();
                await ApplySitesAsync();
                break;

            case "mkcert":
                await Downloader.DownloadFileAsync(version.Url, Paths.MkcertExe, progress);
                await SslManager.EnsureCaInstalledAsync();
                await ApplySitesAsync();
                break;

            default:
                throw new NotSupportedException(version.Component);
        }
    }

    private async Task SwapServerBinariesAsync(ManagedService svc, string binDir, RemoteVersion version,
        IProgress<(long, long)>? progress, bool hasData, string label, string dataFolder)
    {
        var installed = svc.IsInstalled ? await svc.GetInstalledVersionAsync() : "";
        if (installed.Length > 0 && hasData &&
            string.CompareOrdinal(PadV(version.Version), PadV(installed)) < 0)
            throw new InvalidOperationException(
                $"Downgrading {label} ({installed} → {version.Version}) would corrupt the existing databases.\n" +
                $"Export them first, remove {dataFolder}, then install the older version.");

        static int Major(string v) => int.TryParse(v.Split('.')[0], out var m) ? m : 0;
        if (installed.Length > 0 && hasData)
        {
            int oldM = Major(installed), newM = Major(version.Version);
            if (svc.Id == "postgres" && newM != oldM)
                throw new InvalidOperationException(
                    $"PostgreSQL {installed} → {version.Version} changes the major version; the databases in {dataFolder} cannot be opened by the new server.\n" +
                    $"Export first (pg_dumpall), remove {dataFolder}, then install — or keep the current major.");
            if (svc.Id == "mongodb" && newM - oldM > 1)
                throw new InvalidOperationException(
                    $"MongoDB {installed} → {version.Version} skips a major version; mongod refuses to open the data in {dataFolder} without stepping through {oldM + 1}.x first.\n" +
                    $"Install {oldM + 1}.x, start it once, then update again.");
        }

        var wasRunning = svc.IsRunning;
        try
        {
            await Downloader.DownloadAndExtractAsync(version.Url, binDir, progress,
                beforeSwap: wasRunning ? () => svc.StopAsync() : null);
        }
        finally
        {
            if (wasRunning && svc.IsInstalled && !svc.IsRunning) await svc.StartAsync();
        }
    }

    public void ConfigurePhpMyAdmin()
    {
        if (!Directory.Exists(Paths.PhpMyAdminDir)) return;
        var configFile = Path.Combine(Paths.PhpMyAdminDir, "config.inc.php");

        string? secret = null;
        try
        {
            if (File.Exists(configFile))
            {
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(configFile),
                    @"\$cfg\['blowfish_secret'\]\s*=\s*'([^']+)'");
                if (m.Success) secret = m.Groups[1].Value;
            }
        }
        catch { }
        secret ??= Guid.NewGuid().ToString("N");

        static string Q(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?php");
        sb.AppendLine($"$cfg['blowfish_secret'] = '{secret}';");
        sb.AppendLine("$i = 0;");
        void Server(string verbose, int port, string user, string pass)
        {
            sb.AppendLine("$i++;");
            sb.AppendLine($"$cfg['Servers'][$i]['verbose'] = '{verbose}';");
            sb.AppendLine("$cfg['Servers'][$i]['host'] = '127.0.0.1';");
            sb.AppendLine($"$cfg['Servers'][$i]['port'] = {port};");
            sb.AppendLine("$cfg['Servers'][$i]['auth_type'] = 'config';");
            sb.AppendLine($"$cfg['Servers'][$i]['user'] = '{Q(user)}';");
            sb.AppendLine($"$cfg['Servers'][$i]['password'] = '{Q(pass)}';");
            sb.AppendLine("$cfg['Servers'][$i]['AllowNoPassword'] = true;");
        }
        Server("MySQL", Config.MySql.Port, Config.MySql.User, Config.MySql.Password);
        if (MariaDb.IsInstalled)
            Server("MariaDB", Config.MariaDb.Port, Config.MariaDb.User, Config.MariaDb.Password);
        sb.AppendLine($"$cfg['TempDir'] = '{Paths.Ensure(Path.Combine(Paths.Tmp, "phpmyadmin")).Replace('\\', '/')}';");
        File.WriteAllText(configFile, sb.ToString());
    }

    public async Task<string> InstallPhpRedisExtensionAsync(string phpVersion,
        IProgress<(long, long)>? progress = null)
    {
        if (!Platform.IsWindows) return ""; // Built into static Linux PHP builds
        var extDir = Path.Combine(Paths.PhpDir, phpVersion, "ext");
        if (!Directory.Exists(extDir))
            throw new InvalidOperationException($"PHP {phpVersion} is not installed.");

        var remote = await VersionCatalog.FindPhpRedisAsync(phpVersion)
            ?? throw new InvalidOperationException(
                "PECL mirrors are unreachable right now — try again in a few minutes.");

        var tmp = Path.Combine(Paths.Tmp, "phpredis-" + Guid.NewGuid().ToString("N"));
        await Downloader.DownloadAndExtractAsync(remote.Url, tmp, progress);
        try
        {
            var copied = 0;
            foreach (var dll in new[] { "php_igbinary.dll", "php_redis.dll" })
            {
                var src = Directory.GetFiles(tmp, dll, SearchOption.AllDirectories).FirstOrDefault();
                if (src is null) continue;
                File.Copy(src, Path.Combine(extDir, dll), overwrite: true);
                copied++;
            }
            if (copied == 0)
                throw new InvalidOperationException("php_redis.dll was not found in the PECL package.");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }

        var pool = PhpPools.FirstOrDefault(p => p.Settings.Version == phpVersion);
        if (pool is not null)
        {
            pool.WriteIni();
            if (pool.IsRunning) await pool.RestartAsync();
        }
        return remote.Version;
    }

    private static string PadV(string v)
        => string.Join(".", v.Split('.').Select(p => p.PadLeft(6, '0')));

    private void TryApplyShims()
    {
        try { ShimManager.Apply(Config); } catch { }
    }

    private Task ApplyShimsAsync() => Task.Run(TryApplyShims);

    public void SetCurrentPhp(string version)
    {
        Config.DefaultPhpVersion = version;
        Config.Save();
        TryApplyShims();
    }

    public void SetCurrentNode(string version)
    {
        Config.CurrentNodeVersion = version;
        Config.Save();
        TryApplyShims();
    }

    public async Task UpdatePhpAsync(RemoteVersion newVersion, string oldVersion,
        IProgress<(long, long)>? progress = null)
    {
        var pool = PhpPools.FirstOrDefault(p => p.Settings.Version == oldVersion);
        var wasRunning = pool?.IsRunning == true;
        var newDir = Path.Combine(Paths.PhpDir, newVersion.Version);
        var oldDir = Path.Combine(Paths.PhpDir, oldVersion);

        await Downloader.DownloadAndExtractAsync(newVersion.Url, newDir, progress);

        var oldExt = Path.Combine(oldDir, "ext");
        var newExt = Path.Combine(newDir, "ext");
        foreach (var dll in new[] { "php_igbinary.dll", "php_redis.dll" })
        {
            var src = Path.Combine(oldExt, dll);
            var dst = Path.Combine(newExt, dll);
            try
            {
                if (File.Exists(src) && !File.Exists(dst))
                {
                    Directory.CreateDirectory(newExt);
                    File.Copy(src, dst);
                }
            }
            catch { }
        }

        if (pool is not null && wasRunning) await pool.StopAsync();
        try
        {
            var old = Config.Php.FirstOrDefault(p => p.Version == oldVersion);
            if (old is not null) old.Version = newVersion.Version;
            foreach (var site in Config.Sites.Where(s => s.PhpVersion == oldVersion))
                site.PhpVersion = newVersion.Version;
            if (Config.DefaultPhpVersion == oldVersion)
                Config.DefaultPhpVersion = newVersion.Version;
            Config.Save();

            var oldIni = Path.Combine(Paths.PhpEtc, $"php-{oldVersion}.ini");
            try { if (File.Exists(oldIni)) File.Delete(oldIni); } catch { }
            await Task.Run(() =>
            {
                var graveyard = oldDir + OldDirMarker + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                try { Directory.Move(oldDir, graveyard); } catch { return; }
                try { Directory.Delete(graveyard, recursive: true); } catch { }
            });

            SyncPhpPools();
            await ApplyShimsAsync();
            NginxConfigGenerator.WriteAll(Config);
            if (Nginx.IsRunning) await Nginx.ReloadAsync();

            try { await InstallPhpRedisExtensionAsync(newVersion.Version, progress); } catch { }
        }
        finally
        {
            var newPool = PhpPools.FirstOrDefault(p => p.Settings.Version == newVersion.Version);
            if (newPool is not null && wasRunning && !newPool.IsRunning) await newPool.StartAsync();
        }
    }

    public void ImportParkedSites()
    {
        var roots = new List<string> { Paths.Www };
        roots.AddRange(Config.ParkedPaths.Where(p => !string.IsNullOrWhiteSpace(p)));

        var liveRoots = roots
            .Where(Directory.Exists)
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .ToList();
        static bool IsUnder(string path, string root)
            => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var added = Config.Sites.RemoveAll(s =>
        {
            if (ServesNoFiles(s) || string.IsNullOrWhiteSpace(s.RootPath)) return false;
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.ResolvedRoot)); }
            catch { return false; }
            return liveRoots.Any(r => IsUnder(full, r)) && !Directory.Exists(full);
        }) > 0;

        added |= Config.Sites.RemoveAll(s =>
            !string.IsNullOrWhiteSpace(s.RootPath) &&
            string.Equals(
                Path.TrimEndingDirectorySeparator(s.ResolvedRoot),
                Path.TrimEndingDirectorySeparator(Paths.PhpMyAdminDir),
                StringComparison.OrdinalIgnoreCase)) > 0;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.StartsWith('_')) continue;
                var domain = name.ToLowerInvariant() + "." + Config.Tld;
                if (Config.Sites.Any(s => s.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var isStatic = !File.Exists(Path.Combine(dir, "index.php")) &&
                               !File.Exists(Path.Combine(dir, "public", "index.php")) &&
                               File.Exists(Path.Combine(dir, "index.html"));
                var rootPath = File.Exists(Path.Combine(dir, "public", "index.php"))
                    ? Path.Combine(dir, "public")
                    : dir;

                Config.Sites.Add(new Site
                {
                    Name = name,
                    Domain = domain,
                    RootPath = rootPath,
                    Ssl = true,
                    Routes =
                    {
                        new SiteRoute
                        {
                            Path = "/",
                            Kind = isStatic ? RouteKind.Static : RouteKind.Php,
                        }
                    },
                });
                added = true;
            }
        }
        if (added) Config.Save();
    }

    public static bool ServesNoFiles(Site s) =>
        s.Routes.Count > 0 && s.Routes.All(r => r.Kind == RouteKind.Proxy);

    public async Task EnsureComposerAsync()
    {
        if (File.Exists(Paths.ComposerPhar)) return;
        if (!Config.Php.Any()) return;
        var v = await VersionCatalog.GetComposerAsync();
        await Downloader.DownloadFileAsync(v.Url, Paths.ComposerPhar);
        await ApplyShimsAsync();
    }

    public async Task AutoUpdateComposerAsync()
    {
        if (!Config.Php.Any()) return;
        await EnsureComposerAsync();
        var phpExe = Path.Combine(Paths.PhpCurrent, Platform.Exe("php"));
        if (!File.Exists(phpExe)) phpExe = Path.Combine(Paths.PhpCurrent, "bin", Platform.Exe("php"));
        if (!File.Exists(phpExe) || !File.Exists(Paths.ComposerPhar)) return;

        var installed = await ToolVersionCache.GetAsync(Paths.ComposerPhar, async () =>
        {
            var (_, so, se) = await ProcessRunner.RunAsync(
                phpExe, $"\"{Paths.ComposerPhar}\" --version", timeoutMs: 30_000);
            var m = System.Text.RegularExpressions.Regex.Match(so + se, @"Composer version (\d+\.\d+[\d.]*)");
            return m.Success ? m.Groups[1].Value : "";
        });
        if (installed.Length == 0) return;

        var json = await Downloader.GetStringAsync("https://getcomposer.org/versions");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var latest = doc.RootElement.GetProperty("stable")[0].GetProperty("version").GetString();
        if (latest is null) return;
        if (Version.TryParse(latest, out var lv) && Version.TryParse(installed, out var iv) && lv > iv)
            await Downloader.DownloadFileAsync(
                "https://getcomposer.org/composer-stable.phar", Paths.ComposerPhar);
    }

    public async Task EnsureMkcertAsync()
    {
        if (SslManager.IsInstalled) return;
        var v = await VersionCatalog.GetMkcertAsync();
        await Downloader.DownloadFileAsync(v.Url, Paths.MkcertExe);
        await SslManager.EnsureCaInstalledAsync();
    }

    public async Task AutoUpdateMkcertAsync()
    {
        if (!SslManager.IsInstalled) return;
        var installed = await ToolVersionCache.GetAsync(Paths.MkcertExe, async () =>
        {
            var (_, so, se) = await ProcessRunner.RunAsync(Paths.MkcertExe, "-version", timeoutMs: 10_000);
            var m = System.Text.RegularExpressions.Regex.Match(so + se, @"v?(\d+\.\d+[\d.]*)");
            return m.Success ? m.Groups[1].Value : "";
        });
        if (installed.Length == 0) return;
        var latest = await VersionCatalog.GetMkcertAsync();
        if (!Version.TryParse(latest.Version, out var lv) ||
            !Version.TryParse(installed, out var iv) || lv <= iv) return;
        await Downloader.DownloadFileAsync(latest.Url, Paths.MkcertExe);
        await SslManager.EnsureCaInstalledAsync();
    }

    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public async Task ApplySitesAsync()
    {
        await _applyGate.WaitAsync();
        try
        {
            await Task.Run(ImportParkedSites);
            if (Config.Sites.Any(s => s.Ssl))
            {
                try { await EnsureMkcertAsync(); }
                catch { }
            }
            if (SslManager.IsInstalled)
            {
                try { await SslManager.IssueCertificateAsync("localhost"); }
                catch { }
            }
            foreach (var site in Config.Sites.Where(s => s.Ssl))
            {
                try { await SslManager.IssueCertificateAsync(site.Domain, site.Aliases); }
                catch { }
            }
            var hostsEntries = Config.Sites.SelectMany(s =>
                new[] { (s.Domain, s.Name) }
                    .Concat(s.Aliases.Select(a => (a, s.Name)))).ToList();

            await Task.Run(() =>
            {
                HostsFileManager.SyncSites(hostsEntries);
                NginxConfigGenerator.WriteAll(Config);
            });
            if (Nginx.IsRunning) await Nginx.ReloadAsync();
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async Task StartAutoStartServicesAsync(Action<string, Exception>? onError = null)
    {
        var wanted = AllServices.Where(s => s.AutoStart && s.IsInstalled && !s.IsRunning).ToList();
        async Task Run(ManagedService svc)
        {
            try { await svc.StartAsync(); }
            catch (Exception ex) { onError?.Invoke(svc.DisplayName, ex); }
        }

        foreach (var php in wanted.OfType<PhpService>()) await Run(php);
        await Task.WhenAll(wanted
            .Where(s => s is not PhpService && s is not NginxService)
            .Select(Run));
        if (wanted.OfType<NginxService>().FirstOrDefault() is { } nginx) await Run(nginx);
    }

    public async Task StopAllAsync()
    {
        var running = AllServices.Where(s => s.IsRunning).ToList();
        async Task Stop(ManagedService svc) { try { await svc.StopAsync(); } catch { } }
        if (running.OfType<NginxService>().FirstOrDefault() is { } nginx) await Stop(nginx);
        await Task.WhenAll(running.Where(s => s is not NginxService).Select(Stop));
        try
        {
            if (Pm2.Locate() && Pm2.DaemonRunning) await Pm2.KillDaemonAsync();
        }
        catch { }
    }
}
