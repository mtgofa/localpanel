using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

/// <summary>PostgreSQL from the EDB portable binaries zip.</summary>
public class PostgresService : ManagedService
{
    public PostgresService(AppConfig config) : base(config) { }

    public override string Id => "postgres";
    public override string DisplayName => "PostgreSQL";
    private string BinDir => Path.Combine(Paths.PostgresDir, "bin");
    public string ExePath => Path.Combine(BinDir, "postgres.exe");
    private string CtlExe => Path.Combine(BinDir, "pg_ctl.exe");
    private string InitDbExe => Path.Combine(BinDir, "initdb.exe");
    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => Config.Postgres.Port.ToString();

    public override bool AutoStart
    {
        get => Config.Postgres.AutoStart;
        set { Config.Postgres.AutoStart = value; Config.Save(); }
    }

    public bool IsInitialized => File.Exists(Path.Combine(Paths.PostgresData, "PG_VERSION"));

    /// <summary>Apply the configured password to the running server (called on Save).</summary>
    public async Task<string?> ApplyCredentialsAsync(string oldUser, string oldPassword)
    {
        var psql = Path.Combine(BinDir, "psql.exe");
        if (!IsRunning || !File.Exists(psql)) return null;
        var u = Config.Postgres.User.Replace("\"", "\"\"");
        var p = Config.Postgres.Password.Replace("'", "''");
        // Same user: change password. Different user: create it as superuser.
        var sql = string.Equals(u, oldUser, StringComparison.Ordinal)
            ? $"ALTER USER \\\"{u}\\\" WITH PASSWORD '{p}';"
            : $"DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='{u}') THEN CREATE ROLE \\\"{u}\\\" LOGIN SUPERUSER PASSWORD '{p}'; ELSE ALTER USER \\\"{u}\\\" WITH PASSWORD '{p}' SUPERUSER; END IF; END $$;";
        var env = new Dictionary<string, string> { ["PGPASSWORD"] = oldPassword };
        var (code, _, err) = await ProcessRunner.RunAsync(psql,
            $"-h 127.0.0.1 -p {Config.Postgres.Port} -U {oldUser} -d postgres -c \"{sql}\"", BinDir, env, timeoutMs: 30_000);
        return code == 0 ? null : err.Trim();
    }

    public override Task<string> GetInstalledVersionAsync()
        => ToolVersionCache.GetAsync(ExePath, async () =>
        {
            var (_, so, se) = await ProcessRunner.RunAsync(ExePath, "--version", timeoutMs: 15_000);
            var m = System.Text.RegularExpressions.Regex.Match(so + se, @"\d+\.\d+");
            return m.Success ? m.Value : "";
        });

    public async Task InitializeAsync()
    {
        if (IsInitialized) return;
        Paths.Ensure(Paths.PostgresData);
        var pwFile = Path.Combine(Paths.Tmp, "pg-pw.txt");
        File.WriteAllText(pwFile, Config.Postgres.Password);
        try
        {
            var (code, _, err) = await ProcessRunner.RunAsync(InitDbExe,
                $"-D \"{Paths.PostgresData}\" -U {Config.Postgres.User} --pwfile=\"{pwFile}\" -E UTF8 -A md5",
                BinDir, timeoutMs: 180_000);
            if (code != 0)
            {
                // initdb cleans up after itself on an ordinary failure, but on the timeout path
                // (code -1, tree killed) it leaves a partial cluster that has no PG_VERSION yet
                // and would make initdb refuse the non-empty dir forever. Only wipe in that case —
                // never touch a dir that initdb refused because it was already populated.
                if (code == -1 && !IsInitialized)
                    try { Directory.Delete(Paths.PostgresData, recursive: true); } catch { }
                throw new InvalidOperationException("PostgreSQL initialization failed: " + err + Prereqs.Hint);
            }
        }
        finally { try { File.Delete(pwFile); } catch { } }
    }

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("PostgreSQL is not installed yet.");
        if (IsRunning) return;
        if (!IsInitialized) await InitializeAsync();

        var (code, _, err) = await ProcessRunner.RunAsync(CtlExe,
            $"start -D \"{Paths.PostgresData}\" -o \"-p {Config.Postgres.Port} -h 127.0.0.1\" -l \"{LogFile}\" -w -t 60",
            BinDir, timeoutMs: 90_000);
        if (code != 0)
            throw new InvalidOperationException("PostgreSQL failed to start: " + err + Prereqs.Hint);
    }

    public override async Task StopAsync()
    {
        if (!IsInstalled) return;
        await ProcessRunner.RunAsync(CtlExe,
            $"stop -D \"{Paths.PostgresData}\" -m fast -w -t 30", BinDir, timeoutMs: 45_000);
    }

    public override bool IsRunning
    {
        get
        {
            // pg_ctl leaves postmaster.pid in the data dir while the server runs.
            var pidFile = Path.Combine(Paths.PostgresData, "postmaster.pid");
            if (!File.Exists(pidFile)) return false;
            try
            {
                var first = File.ReadLines(pidFile).FirstOrDefault();
                return int.TryParse(first, out var pid) && ProcessRunner.IsAlive(pid, "postgres");
            }
            catch { return false; }
        }
    }
}
