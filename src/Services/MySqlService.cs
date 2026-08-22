using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

public class MySqlService : ManagedService
{
    public MySqlService(AppConfig config) : base(config) { }

    public override string Id => "mysql";
    public override string DisplayName => "MySQL";
    public string ExePath => Path.Combine(Paths.MySqlDir, "bin", "mysqld.exe");
    private string AdminExe => Path.Combine(Paths.MySqlDir, "bin", "mysqladmin.exe");
    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => Config.MySql.Port.ToString();

    public override bool AutoStart
    {
        get => Config.MySql.AutoStart;
        set { Config.MySql.AutoStart = value; Config.Save(); }
    }

    private string IniFile => Path.Combine(Paths.MySqlEtc, "my.ini");

    private void WriteIni()
    {
        var ini = $"""
            [mysqld]
            basedir="{Paths.MySqlDir.Replace('\\', '/')}"
            datadir="{Paths.MySqlData.Replace('\\', '/')}"
            port={Config.MySql.Port}
            bind-address=127.0.0.1
            max_connections=151
            log-error="{Path.Combine(Paths.Logs, "mysql-error.log").Replace('\\', '/')}"

            [client]
            port={Config.MySql.Port}
            """;
        File.WriteAllText(IniFile, ini);
    }

    public bool IsInitialized => Directory.Exists(Paths.MySqlData) &&
                                 Directory.EnumerateFileSystemEntries(Paths.MySqlData).Any();

    /// <summary>Apply the configured user/password to the running server (called on Save).</summary>
    public async Task<string?> ApplyCredentialsAsync(string oldUser, string oldPassword)
    {
        var cli = Path.Combine(Paths.MySqlDir, "bin", "mysql.exe");
        if (!IsRunning || !File.Exists(cli)) return null;
        var u = Config.MySql.User.Replace("'", "''");
        var p = Config.MySql.Password.Replace("'", "''");
        var sql = $"CREATE USER IF NOT EXISTS '{u}'@'localhost' IDENTIFIED BY '{p}'; " +
                  $"CREATE USER IF NOT EXISTS '{u}'@'127.0.0.1' IDENTIFIED BY '{p}'; " +
                  $"ALTER USER '{u}'@'localhost' IDENTIFIED BY '{p}'; " +
                  $"ALTER USER '{u}'@'127.0.0.1' IDENTIFIED BY '{p}'; " +
                  $"GRANT ALL PRIVILEGES ON *.* TO '{u}'@'localhost' WITH GRANT OPTION; " +
                  $"GRANT ALL PRIVILEGES ON *.* TO '{u}'@'127.0.0.1' WITH GRANT OPTION; FLUSH PRIVILEGES;";
        var auth = $"-u {oldUser}" + (oldPassword.Length > 0 ? $" -p{oldPassword}" : "");
        var (code, _, err) = await ProcessRunner.RunAsync(cli,
            $"--host=127.0.0.1 --port={Config.MySql.Port} {auth} -e \"{sql}\"", timeoutMs: 30_000);
        return code == 0 ? null : err.Trim();
    }

    public override Task<string> GetInstalledVersionAsync()
        => ToolVersionCache.GetAsync(ExePath, async () =>
        {
            var (_, so, se) = await ProcessRunner.RunAsync(ExePath, "--version", timeoutMs: 15_000);
            var m = System.Text.RegularExpressions.Regex.Match(so + se, @"Ver ([\d.]+)");
            return m.Success ? m.Groups[1].Value : "";
        });

    public async Task InitializeAsync()
    {
        WriteIni();
        Paths.Ensure(Paths.MySqlData);
        if (IsInitialized) return;
        // Root with empty password — standard for local dev (same as Laragon/XAMPP).
        var (code, _, err) = await ProcessRunner.RunAsync(
            ExePath, $"--defaults-file=\"{IniFile}\" --initialize-insecure --console",
            Paths.MySqlDir, timeoutMs: 180_000);
        if (code != 0)
        {
            // A partial datadir would pass IsInitialized forever and mysqld refuses to
            // re-initialize a non-empty dir — remove it (best-effort) so the next start retries.
            try { Directory.Delete(Paths.MySqlData, recursive: true); } catch { }
            throw new InvalidOperationException("MySQL initialization failed: " + err + Prereqs.Hint);
        }
    }

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("MySQL is not installed yet.");
        if (IsRunning) return;
        WriteIni();
        if (!IsInitialized) await InitializeAsync();

        var p = ProcessRunner.StartBackground(
            ExePath, $"--defaults-file=\"{IniFile}\" --console",
            Path.Combine(Paths.MySqlDir, "bin"), logFile: LogFile);
        ProcessRunner.WritePidFile(Id, p.Id);
        // Remember the port we actually bound so StopAsync targets it even if config changes later.
        ProcessRunner.WritePortFile(Id, Config.MySql.Port);
        await Task.Delay(1500);
        if (p.HasExited)
        {
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException("MySQL exited immediately — check logs/mysql.log" + Prereqs.Hint);
        }
    }

    public override async Task StopAsync()
    {
        var pids = ProcessRunner.ReadPidFile(Id);
        var graceful = File.Exists(AdminExe);
        if (graceful)
        {
            // Graceful shutdown protects the data files. Target the port the server was
            // STARTED with — the config port may already hold the user's new value.
            var port = ProcessRunner.ReadPortFile(Id, Config.MySql.Port);
            var (code, _, _) = await ProcessRunner.RunAsync(AdminExe,
                $"--port={port} --host=127.0.0.1 -u {Config.MySql.User}" + (Config.MySql.Password.Length > 0 ? $" -p{Config.MySql.Password}" : "") + " shutdown",
                timeoutMs: 30_000);
            graceful = code == 0; // request refused (bad credentials / port) → no point waiting
        }
        // mysqladmin returns as soon as the request is accepted; an InnoDB shutdown with a
        // warm buffer pool can take several seconds. Poll before resorting to a hard kill.
        for (var i = 0; graceful && i < 120 && pids.Any(p => ProcessRunner.IsAlive(p, "mysqld")); i++)
            await Task.Delay(250);
        foreach (var pid in pids)
            if (ProcessRunner.IsAlive(pid, "mysqld")) ProcessRunner.KillPid(pid);
        ProcessRunner.DeletePidFile(Id);
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Any(p => ProcessRunner.IsAlive(p, "mysqld"));
        }
    }
}
