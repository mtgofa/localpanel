using System.IO;
using System.Text.Json;

namespace LocalPanel.Core;

public record Pm2Process(int Id, string Name, string Status, string Port, double CpuPercent, long MemoryBytes, int? Pid, int Restarts)
{
    public bool IsOnline => Status == "online";
    public string ToggleText => IsOnline ? "Stop" : "Start";
    public string PortText => Port.Length > 0 ? "Port " + Port : "";
    public string StatsText => IsOnline ? $"{CpuPercent:0.#}%  ·  {MemoryBytes / 1048576} MB" : Status;
}

/// <summary>
/// Talks to pm2 through its CLI (pm2 jlist / stop / restart / delete / logs).
/// pm2 is found either in one of our portable Node versions or on the PATH.
/// </summary>
public class Pm2Manager
{
    public string? Pm2Cmd { get; private set; }
    public string? NodeBinDir { get; private set; }

    public bool Locate()
    {
        // Look for pm2 installed globally inside a portable Node version.
        if (Directory.Exists(Paths.NodeDir))
        {
            foreach (var nodeVer in Directory.GetDirectories(Paths.NodeDir)
                         .OrderByDescending(Path.GetFileName))
            {
                var cmd = Path.Combine(nodeVer, "pm2.cmd");
                if (File.Exists(cmd)) { Pm2Cmd = cmd; NodeBinDir = nodeVer; return true; }
            }
        }
        // Fall back to a system-wide pm2 on the PATH.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var cmd = Path.Combine(dir.Trim(), "pm2.cmd");
            if (File.Exists(cmd)) { Pm2Cmd = cmd; NodeBinDir = dir.Trim(); return true; }
        }
        Pm2Cmd = null;
        return false;
    }

    private Dictionary<string, string> Env => new()
    {
        // Make sure pm2 finds node.exe next to it, and keep its home portable.
        ["PATH"] = (NodeBinDir is null ? "" : NodeBinDir + ";") + Environment.GetEnvironmentVariable("PATH"),
        ["PM2_HOME"] = Paths.Ensure(Path.Combine(Paths.Data, "pm2")),
    };

    public async Task<List<Pm2Process>> ListAsync()
    {
        if (Pm2Cmd is null && !Locate()) return new List<Pm2Process>();
        var (code, stdout, _) = await ProcessRunner.RunAsync(Pm2Cmd!, "jlist", env: Env, timeoutMs: 30_000);
        if (code != 0) return new List<Pm2Process>();

        // pm2 may print banner lines before the JSON array.
        var jsonStart = stdout.IndexOf('[');
        if (jsonStart < 0) return new List<Pm2Process>();
        try
        {
            using var doc = JsonDocument.Parse(stdout[jsonStart..]);
            var result = new List<Pm2Process>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var env2 = el.GetProperty("pm2_env");
                var monit = el.GetProperty("monit");
                var port = "";
                if (env2.TryGetProperty("env", out var appEnv) &&
                    appEnv.ValueKind == JsonValueKind.Object &&
                    appEnv.TryGetProperty("PORT", out var portEl))
                    port = portEl.ValueKind == JsonValueKind.Number
                        ? portEl.GetRawText() : portEl.GetString() ?? "";
                result.Add(new Pm2Process(
                    Id: el.GetProperty("pm_id").GetInt32(),
                    Name: el.GetProperty("name").GetString() ?? "?",
                    Status: env2.GetProperty("status").GetString() ?? "?",
                    Port: port,
                    CpuPercent: monit.GetProperty("cpu").GetDouble(),
                    MemoryBytes: monit.GetProperty("memory").GetInt64(),
                    Pid: el.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number
                        ? pid.GetInt32() : null,
                    Restarts: env2.TryGetProperty("restart_time", out var r) ? r.GetInt32() : 0));
            }
            return result;
        }
        catch { return new List<Pm2Process>(); }
    }

    /// <summary>The pm2 daemon (background broker) — detected via its pid file.</summary>
    public bool DaemonRunning
    {
        get
        {
            var pidFile = Path.Combine(Paths.Data, "pm2", "pm2.pid");
            if (!File.Exists(pidFile)) return false;
            try
            {
                return int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) &&
                       ProcessRunner.IsAlive(pid);
            }
            catch { return false; }
        }
    }

    public async Task<string> VersionAsync()
    {
        var (code, so, _) = await RunAsync("-v");
        if (code != 0) return "";
        var m = System.Text.RegularExpressions.Regex.Match(so, @"\d+\.\d+[\d.]*");
        return m.Success ? m.Value : "";
    }

    public Task<(int, string, string)> KillDaemonAsync() => RunAsync("kill");

    /// <summary>Latest pm2 version on npm (null when offline / npm missing).</summary>
    public async Task<string?> NpmLatestAsync()
    {
        if (Pm2Cmd is null && !Locate()) return null;
        var npm = Path.Combine(NodeBinDir ?? "", "npm.cmd");
        if (!File.Exists(npm)) npm = Path.Combine(Paths.NodeCurrent, "npm.cmd");
        if (!File.Exists(npm)) return null;
        var (code, so, _) = await ProcessRunner.RunAsync(npm, "view pm2 version", env: Env, timeoutMs: 30_000);
        if (code != 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(so, @"\d+\.\d+[\d.]*");
        return m.Success ? m.Value : null;
    }

    public Task<(int, string, string)> StartAppAsync(int id) => RunAsync($"start {id}");
    public Task<(int, string, string)> StopAsync(int id) => RunAsync($"stop {id}");
    public Task<(int, string, string)> RestartAsync(int id) => RunAsync($"restart {id}");
    public Task<(int, string, string)> DeleteAsync(int id) => RunAsync($"delete {id}");

    public async Task<string> LogsAsync(int id, int lines = 100)
    {
        var (_, stdout, stderr) = await RunAsync($"logs {id} --lines {lines} --nostream");
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private async Task<(int, string, string)> RunAsync(string args)
    {
        if (Pm2Cmd is null && !Locate())
            return (-1, "", "pm2 was not found in any Node version or on PATH.");
        return await ProcessRunner.RunAsync(Pm2Cmd!, args, env: Env, timeoutMs: 60_000);
    }
}
