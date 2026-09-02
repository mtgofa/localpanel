using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

/// <summary>MariaDB — MySQL-compatible, runs on its own port beside MySQL.</summary>
public class MariaDbService : ManagedService
{
    public MariaDbService(AppConfig config) : base(config) { }

    public override string Id => "mariadb";
    public override string DisplayName => "MariaDB";
    public string ExePath => Path.Combine(Paths.MariaDbDir, "bin", Platform.Exe("mariadbd"));
    private string AdminExe => Path.Combine(Paths.MariaDbDir, "bin", Platform.Exe("mariadb-admin"));
    private string InstallDbExe => Path.Combine(Paths.MariaDbDir, "bin", Platform.Exe("mariadb-install-db"));
    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => Config.MariaDb.Port.ToString();

    public override bool AutoStart
    {
        get => Config.MariaDb.AutoStart;
        set { Config.MariaDb.AutoStart = value; Config.Save(); }
    }

    private string IniFile => Path.Combine(Paths.MariaDbEtc, "my.cnf");

    private void WriteIni()
    {
        File.WriteAllText(IniFile, $"""
            [mysqld]
            basedir="{Paths.MariaDbDir.Replace('\\', '/')}"
            datadir="{Paths.MariaDbData.Replace('\\', '/')}"
            port={Config.MariaDb.Port}
            bind-address=127.0.0.1
            log-error="{Path.Combine(Paths.Logs, "mariadb-error.log").Replace('\\', '/')}"

            [client]
            port={Config.MariaDb.Port}
            """);
    }

    public bool IsInitialized => Directory.Exists(Paths.MariaDbData) &&
                                 Directory.EnumerateFileSystemEntries(Paths.MariaDbData).Any();

    public async Task<string?> ApplyCredentialsAsync(string oldUser, string oldPassword)
    {
        var cli = Path.Combine(Paths.MariaDbDir, "bin", Platform.Exe("mariadb"));
        if (!IsRunning || !File.Exists(cli)) return null;
        var u = Config.MariaDb.User.Replace("'", "''");
        var p = Config.MariaDb.Password.Replace("'", "''");
        var sql = $"CREATE USER IF NOT EXISTS '{u}'@'localhost' IDENTIFIED BY '{p}'; " +
                  $"CREATE USER IF NOT EXISTS '{u}'@'127.0.0.1' IDENTIFIED BY '{p}'; " +
                  $"ALTER USER '{u}'@'localhost' IDENTIFIED BY '{p}'; " +
                  $"ALTER USER '{u}'@'127.0.0.1' IDENTIFIED BY '{p}'; " +
                  $"GRANT ALL PRIVILEGES ON *.* TO '{u}'@'localhost' WITH GRANT OPTION; " +
                  $"GRANT ALL PRIVILEGES ON *.* TO '{u}'@'127.0.0.1' WITH GRANT OPTION; FLUSH PRIVILEGES;";
        var auth = $"-u {oldUser}" + (oldPassword.Length > 0 ? $" -p{oldPassword}" : "");
        var (code, _, err) = await ProcessRunner.RunAsync(cli,
            $"--host=127.0.0.1 --port={Config.MariaDb.Port} {auth} -e \"{sql}\"", timeoutMs: 30_000);
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
        Paths.Ensure(Paths.MariaDbData);
        if (IsInitialized) return;
        var (code, _, err) = await ProcessRunner.RunAsync(
            InstallDbExe, $"--datadir=\"{Paths.MariaDbData}\" --port={Config.MariaDb.Port}",
            Path.Combine(Paths.MariaDbDir, "bin"), timeoutMs: 180_000);
        if (code != 0)
        {
            try { Directory.Delete(Paths.MariaDbData, recursive: true); } catch { }
            throw new InvalidOperationException("MariaDB initialization failed: " + err + Prereqs.Hint);
        }
    }

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("MariaDB is not installed yet.");
        if (IsRunning) return;
        WriteIni();
        if (!IsInitialized) await InitializeAsync();

        var p = ProcessRunner.StartBackground(
            ExePath, $"--defaults-file=\"{IniFile}\" --console",
            Path.Combine(Paths.MariaDbDir, "bin"), logFile: LogFile);
        ProcessRunner.WritePidFile(Id, p.Id);
        ProcessRunner.WritePortFile(Id, Config.MariaDb.Port);
        await Task.Delay(1500);
        if (p.HasExited)
        {
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException("MariaDB exited immediately — check logs/mariadb.log" + Prereqs.Hint);
        }
    }

    public override async Task StopAsync()
    {
        var pids = ProcessRunner.ReadPidFile(Id);
        var graceful = File.Exists(AdminExe);
        if (graceful)
        {
            var port = ProcessRunner.ReadPortFile(Id, Config.MariaDb.Port);
            var (code, _, _) = await ProcessRunner.RunAsync(AdminExe,
                $"--port={port} --host=127.0.0.1 -u {Config.MariaDb.User}" + (Config.MariaDb.Password.Length > 0 ? $" -p{Config.MariaDb.Password}" : "") + " shutdown", timeoutMs: 30_000);
            graceful = code == 0;
        }
        for (var i = 0; graceful && i < 120 && pids.Any(p => ProcessRunner.IsAlive(p, "mariadbd")); i++)
            await Task.Delay(250);
        foreach (var pid in pids)
            if (ProcessRunner.IsAlive(pid, "mariadbd")) ProcessRunner.KillPid(pid);
        ProcessRunner.DeletePidFile(Id);
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Any(p => ProcessRunner.IsAlive(p, "mariadbd"));
        }
    }
}
