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
    public List<PhpService> PhpPools { get; } = new();
    public Pm2Manager Pm2 { get; } = new();

    public ServiceManager()
    {
        Config = AppConfig.Load();
        Nginx = new NginxService(Config);
        MySql = new MySqlService(Config);
        Redis = new RedisService(Config);
        SyncPhpPools();
    }

    public IEnumerable<ManagedService> AllServices
    {
        get
        {
            yield return Nginx;
            yield return MySql;
            yield return Redis;
            foreach (var p in PhpPools) yield return p;
        }
    }

    private void SyncPhpPools()
    {
        PhpPools.Clear();
        // Drop config entries whose folder was deleted, discover new folders.
        Config.Php.RemoveAll(p => !Directory.Exists(Path.Combine(Paths.PhpDir, p.Version)));
        foreach (var dir in Paths.VersionDirs(Paths.PhpDir))
        {
            var version = Path.GetFileName(dir);
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
                TryApplyShims();
                // Laravel expects phpredis; official builds do not bundle it.
                try { await InstallPhpRedisExtensionAsync(version.Version, progress); } catch { }
                break;

            case "node":
                await Downloader.DownloadAndExtractAsync(version.Url,
                    Path.Combine(Paths.NodeDir, version.Version), progress);
                Config.CurrentNodeVersion ??= version.Version;
                Config.Save();
                TryApplyShims();
                break;

            case "composer":
                await Downloader.DownloadFileAsync(version.Url, Paths.ComposerPhar, progress);
                TryApplyShims();
                break;

            case "nginx":
                await Downloader.DownloadAndExtractAsync(version.Url, Paths.NginxDir, progress);
                await ApplySitesAsync();
                break;

            case "mysql":
                await Downloader.DownloadAndExtractAsync(version.Url, Paths.MySqlDir, progress);
                break;

            case "redis":
                await Downloader.DownloadAndExtractAsync(version.Url, Paths.RedisDir, progress);
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

    private void ConfigurePhpMyAdmin()
    {
        // Auto-login as root (local dev): auth_type 'config' skips the login page.
        var blowfish = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(Paths.PhpMyAdminDir, "config.inc.php"), $$"""
            <?php
            $cfg['blowfish_secret'] = '{{blowfish}}';
            $i = 1;
            $cfg['Servers'][$i]['host'] = '127.0.0.1';
            $cfg['Servers'][$i]['port'] = {{Config.MySql.Port}};
            $cfg['Servers'][$i]['auth_type'] = 'config';
            $cfg['Servers'][$i]['user'] = 'root';
            $cfg['Servers'][$i]['password'] = '';
            $cfg['Servers'][$i]['AllowNoPassword'] = true;
            $cfg['TempDir'] = '{{Paths.Ensure(Path.Combine(Paths.Tmp, "phpmyadmin")).Replace('\\', '/')}}';
            """);

        // Register the phpmyadmin.<tld> site if it does not exist yet.
        var domain = "phpmyadmin." + Config.Tld;
        if (Config.Sites.All(s => s.Domain != domain))
        {
            Config.Sites.Add(new Site
            {
                Name = "phpMyAdmin",
                Domain = domain,
                RootPath = Paths.PhpMyAdminDir,
                Type = SiteType.Php,
                Ssl = true,
            });
            Config.Save();
        }
    }

    /// <summary>
    /// Download the PECL phpredis build for this PHP version into its ext\
    /// folder, regenerate the ini (which auto-enables it), restart the pool.
    /// </summary>
    public async Task<string> InstallPhpRedisExtensionAsync(string phpVersion,
        IProgress<(long, long)>? progress = null)
    {
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

    private void TryApplyShims()
    {
        try { ShimManager.Apply(Config); } catch { /* shims are best-effort */ }
    }

    /// <summary>Set the global/current PHP (repoints bin\php\current + shims).</summary>
    public void SetCurrentPhp(string version)
    {
        Config.DefaultPhpVersion = version;
        Config.Save();
        TryApplyShims();
    }

    /// <summary>nvm-style "use": repoints bin\node\current at this version.</summary>
    public void SetCurrentNode(string version)
    {
        Config.CurrentNodeVersion = version;
        Config.Save();
        TryApplyShims();
    }

    /// <summary>
    /// Update PHP in place: install the newer patch, carry over pool settings and
    /// site bindings from the old version, then remove the old folder.
    /// </summary>
    public async Task UpdatePhpAsync(RemoteVersion newVersion, string oldVersion,
        IProgress<(long, long)>? progress = null)
    {
        var pool = PhpPools.FirstOrDefault(p => p.Settings.Version == oldVersion);
        var wasRunning = pool?.IsRunning == true;
        if (pool is not null && wasRunning) await pool.StopAsync();

        await Downloader.DownloadAndExtractAsync(newVersion.Url,
            Path.Combine(Paths.PhpDir, newVersion.Version), progress);

        // Move the old pool's settings (ports, workers, limits) onto the new version.
        var old = Config.Php.FirstOrDefault(p => p.Version == oldVersion);
        if (old is not null) old.Version = newVersion.Version;
        foreach (var site in Config.Sites.Where(s => s.PhpVersion == oldVersion))
            site.PhpVersion = newVersion.Version;
        if (Config.DefaultPhpVersion == oldVersion)
            Config.DefaultPhpVersion = newVersion.Version;
        Config.Save();

        // Drop the old ini (regenerated with the new paths) and the old binaries.
        var oldIni = Path.Combine(Paths.PhpEtc, $"php-{oldVersion}.ini");
        try { if (File.Exists(oldIni)) File.Delete(oldIni); } catch { }
        try { Directory.Delete(Path.Combine(Paths.PhpDir, oldVersion), recursive: true); } catch { }

        SyncPhpPools();
        TryApplyShims();
        NginxConfigGenerator.WriteAll(Config);
        if (Nginx.IsRunning) await Nginx.ReloadAsync();

        var newPool = PhpPools.FirstOrDefault(p => p.Settings.Version == newVersion.Version);
        if (newPool is not null && wasRunning) await newPool.StartAsync();
    }

    // ---------------- sites ----------------

    /// <summary>
    /// Herd-style parking: every subfolder of www and the configured parked
    /// paths becomes a site automatically (existing domains are untouched).
    /// </summary>
    public void ImportParkedSites()
    {
        var roots = new List<string> { Paths.Www };
        roots.AddRange(Config.ParkedPaths.Where(p => !string.IsNullOrWhiteSpace(p)));

        // The folder is the source of truth: a site whose folder was deleted
        // disappears automatically (proxy sites have no folder and are kept).
        var added = Config.Sites.RemoveAll(s =>
            s.Type != SiteType.Proxy &&
            !string.IsNullOrWhiteSpace(s.RootPath) &&
            !Directory.Exists(s.ResolvedRoot)) > 0;
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

                // Laravel-style apps serve from public\; plain sites from the folder itself.
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
                    Type = isStatic ? SiteType.Static : SiteType.Php,
                    Ssl = true,
                });
                added = true;
            }
        }
        if (added) Config.Save();
    }

    /// <summary>Park-import, issue certificates, regenerate nginx config, sync hosts, reload nginx.</summary>
    public async Task ApplySitesAsync()
    {
        ImportParkedSites();
        foreach (var site in Config.Sites.Where(s => s.Ssl))
        {
            try { await SslManager.IssueCertificateAsync(site.Domain, site.Aliases); }
            catch { /* cert failure should not block http */ }
        }
        HostsFileManager.SyncSites(Config.Sites.SelectMany(s =>
            new[] { (s.Domain, s.Name) }
                .Concat(s.Aliases.Select(a => (a, s.Name)))));
        NginxConfigGenerator.WriteAll(Config);
        if (Nginx.IsRunning) await Nginx.ReloadAsync();
    }

    // ---------------- lifecycle ----------------

    public async Task StartAutoStartServicesAsync(Action<string, Exception>? onError = null)
    {
        foreach (var svc in AllServices.Where(s => s.AutoStart && s.IsInstalled && !s.IsRunning))
        {
            try { await svc.StartAsync(); }
            catch (Exception ex) { onError?.Invoke(svc.DisplayName, ex); }
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var svc in AllServices.Where(s => s.IsRunning))
        {
            try { await svc.StopAsync(); } catch { }
        }
    }
}
