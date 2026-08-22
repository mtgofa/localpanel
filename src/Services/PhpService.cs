using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

/// <summary>
/// One FastCGI pool per installed PHP version: N php-cgi.exe workers on
/// consecutive ports (9000, 9001, ...). Nginx load-balances across them
/// through a generated upstream block.
/// </summary>
public class PhpService : ManagedService
{
    public PhpVersionSettings Settings { get; }

    public PhpService(AppConfig config, PhpVersionSettings settings) : base(config)
        => Settings = settings;

    public override string Id => "php-" + Settings.Version;
    public override string DisplayName => "PHP " + Settings.Version;
    public string VersionDir => Path.Combine(Paths.PhpDir, Settings.Version);
    public string ExePath => Path.Combine(VersionDir, "php-cgi.exe");
    public override bool IsInstalled => File.Exists(ExePath);
    // FastCGI plumbing is auto-managed; no need to surface ports in the UI.
    public override string PortInfo => "";

    public override bool AutoStart
    {
        get => Settings.AutoStart;
        set { Settings.AutoStart = value; Config.Save(); }
    }

    public string IniFile => Path.Combine(Paths.PhpEtc, $"php-{Settings.Version}.ini");

    public static readonly string[] DefaultExtensions =
    {
        "curl", "exif", "fileinfo", "ftp", "gd", "intl", "mbstring", "mysqli",
        "openssl", "pdo_mysql", "pdo_sqlite", "sodium", "sqlite3", "zip",
    };

    /// <summary>Every php_*.dll shipped in this version's ext folder.</summary>
    public List<string> AvailableExtensions()
    {
        var extDir = Path.Combine(VersionDir, "ext");
        if (!Directory.Exists(extDir)) return new List<string>();
        return Directory.GetFiles(extDir, "php_*.dll")
            .Select(f => Path.GetFileNameWithoutExtension(f)[4..])
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Chosen (or default) extensions plus auto extras; igbinary before redis.</summary>
    public List<string> EffectiveExtensions()
    {
        var list = (Settings.EnabledExtensions is { Count: > 0 }
            ? Settings.EnabledExtensions : DefaultExtensions.ToList()).Distinct().ToList();
        var extDir = Path.Combine(VersionDir, "ext");
        foreach (var extra in new[] { "igbinary", "redis" })
            if (File.Exists(Path.Combine(extDir, $"php_{extra}.dll")) && !list.Contains(extra))
                list.Add(extra);
        if (list.Contains("igbinary") && list.Contains("redis"))
        {
            list.Remove("igbinary");
            list.Insert(list.IndexOf("redis"), "igbinary");
        }
        return list;
    }

    public void EnsureIni()
    {
        // Regenerate when missing OR when the folder was moved (the ini stores
        // absolute paths, so a stale extension_dir would break every extension).
        if (!File.Exists(IniFile)) { WriteIni(); return; }
        try
        {
            if (!File.ReadAllText(IniFile).Contains(VersionDir, StringComparison.OrdinalIgnoreCase))
                WriteIni();
        }
        catch { }
    }

    /// <summary>Disk-backed opcache for this version (shared by every worker in the pool).</summary>
    public string OpcacheDir => Path.Combine(Paths.Tmp, "opcache", Settings.Version);

    /// <summary>(Re)generate the php.ini from the bundled template plus the configured limits.</summary>
    public void WriteIni()
    {
        var template = Path.Combine(VersionDir, "php.ini-development");
        var content = File.Exists(template) ? File.ReadAllText(template) : "";
        // PHP writes into the file cache but does not create the root folder itself.
        var opcacheDir = Paths.Ensure(OpcacheDir).Replace(Path.DirectorySeparatorChar, '/');

        var extDir = Path.Combine(VersionDir, "ext");
        content = content.Replace(";extension_dir = \"ext\"", $"extension_dir = \"{extDir}\"");

        // Enable exactly the chosen extension set (template lines are commented out).
        foreach (var ext in EffectiveExtensions())
        {
            var esc = System.Text.RegularExpressions.Regex.Escape(ext);
            var replaced = System.Text.RegularExpressions.Regex.Replace(content,
                $@"(?m)^;extension={esc}\s*$", $"extension={ext}");
            if (replaced != content)
                content = replaced;
            else if (!System.Text.RegularExpressions.Regex.IsMatch(content, $@"(?m)^extension={esc}\s*$"))
                content += $"{Environment.NewLine}extension={ext}";
        }

        content += $"""


            ; ---- LocalPanel overrides ----
            memory_limit = {Settings.MemoryLimitMb}M
            upload_max_filesize = {Settings.UploadMaxMb}M
            post_max_size = {Settings.UploadMaxMb}M
            max_execution_time = {Settings.MaxExecutionSeconds}
            max_input_vars = {Settings.MaxInputVars}
            date.timezone = "{Settings.TimeZone}"
            display_errors = {(Settings.DisplayErrors ? "On" : "Off")}
            error_log = "{Path.Combine(Paths.Logs, $"php-{Settings.Version}-error.log")}"

            ; ---- speed (safe for development) ----
            ; opcache with validate_timestamps=1 + revalidate_freq=0 re-checks every file on
            ; every request, so edits show up instantly — but Laravel's thousands of files are
            ; not re-parsed each time. This is the single biggest win on Windows.
            zend_extension = opcache
            opcache.enable = 1
            opcache.enable_cli = 0
            opcache.validate_timestamps = 1
            opcache.revalidate_freq = 0
            opcache.memory_consumption = 256
            opcache.interned_strings_buffer = 16
            opcache.max_accelerated_files = 20000
            ; Second-level cache on disk: the shared memory cache is empty again after every
            ; pool restart, and re-parsing a Laravel tree costs seconds on Windows. With the
            ; file cache the workers load compiled opcodes back instead of recompiling, and
            ; all workers of the pool share the same folder.
            opcache.file_cache = "{opcacheDir}"
            opcache.file_cache_consistency_checks = 1
            realpath_cache_size = 4096K
            realpath_cache_ttl = 600
            """;

        // Route PHP's mail() into the local Mailpit catcher when it is installed.
        if (File.Exists(Paths.MailpitExe))
            content += $"""

                SMTP = 127.0.0.1
                smtp_port = {Config.Mailpit.SmtpPort}
                """;
        File.WriteAllText(IniFile, content);
        // Also drop a copy next to php.exe: the CLI (terminal shims, Composer, artisan,
        // IDEs) auto-loads <phpdir>php.ini with no flags, so extensions and limits
        // match the web pool exactly.
        try { File.WriteAllText(Path.Combine(VersionDir, "php.ini"), content); } catch { }
    }

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException($"PHP {Settings.Version} is not installed yet.");
        var existing = ProcessRunner.ReadPidFile(Id);
        if (existing.Length > 0 && existing.All(p => ProcessRunner.IsAlive(p, "php-cgi"))) return; // pool fully up
        // Partial pool (a worker died / stale pid file): reap the survivors first so their
        // ports and pids are recycled instead of being overwritten and orphaned.
        if (existing.Length > 0) await StopAsync();
        EnsureIni();

        var env = new Dictionary<string, string>
        {
            // 0 = never recycle; php-cgi exits after N requests otherwise.
            ["PHP_FCGI_MAX_REQUESTS"] = "0",
        };

        var pids = new List<int>();
        for (var i = 0; i < Settings.Workers; i++)
        {
            var port = Settings.FastCgiPort + i;
            var p = ProcessRunner.StartBackground(
                ExePath, $"-b 127.0.0.1:{port} -c \"{IniFile}\"",
                VersionDir, env, logFile: LogFile);
            pids.Add(p.Id);
        }
        ProcessRunner.WritePidFile(Id, pids.ToArray());
        await Task.Delay(400);
        if (pids.Any(p => !ProcessRunner.IsAlive(p, "php-cgi")))
        {
            // Kill the workers that did bind before dropping the pid file, otherwise they are orphaned.
            foreach (var pid in pids) ProcessRunner.KillPid(pid);
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException(
                $"PHP {Settings.Version} workers exited immediately." + Prereqs.Hint);
        }
    }

    public override Task StopAsync()
    {
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
            if (ProcessRunner.IsAlive(pid, "php-cgi")) ProcessRunner.KillPid(pid);
        ProcessRunner.DeletePidFile(Id);
        return Task.CompletedTask;
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            // Any, not All: a degraded pool still counts as running so Stop/Restart/Exit reap the survivors.
            return pids.Length > 0 && pids.Any(p => ProcessRunner.IsAlive(p, "php-cgi"));
        }
    }
}
