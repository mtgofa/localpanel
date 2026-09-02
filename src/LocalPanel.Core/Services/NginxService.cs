using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

public class NginxService : ManagedService
{
    public NginxService(AppConfig config) : base(config) { }

    public override string Id => "nginx";
    public override string DisplayName => "Nginx";

    public string ExePath
    {
        get
        {
            var p1 = Path.Combine(Paths.NginxDir, Platform.Exe("nginx"));
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(Paths.NginxDir, "sbin", Platform.Exe("nginx"));
            if (File.Exists(p2)) return p2;
            return p1;
        }
    }

    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => $"{Config.Nginx.HttpPort} / {Config.Nginx.HttpsPort} (ssl)";

    public override bool AutoStart
    {
        get => Config.Nginx.AutoStart;
        set { Config.Nginx.AutoStart = value; Config.Save(); }
    }

    private string Prefix => Paths.NginxEtc.Replace('\\', '/');
    private string ConfArgs =>
        $"-p \"{Prefix}/\" -c \"{Path.Combine(Paths.NginxEtc, "nginx.conf").Replace('\\', '/')}\"";

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("Nginx is not installed yet.");
        if (IsRunning) return;

        Paths.Ensure(Path.Combine(Paths.NginxEtc, "logs"));
        NginxConfigGenerator.WriteAll(Config);

        try { if (File.Exists(MasterPidFile)) File.Delete(MasterPidFile); } catch { }
        var p = ProcessRunner.StartBackground(ExePath, ConfArgs, Paths.NginxDir, logFile: LogFile);
        ProcessRunner.WritePidFile(Id, p.Id);

        var started = false;
        for (var i = 0; i < 30 && !p.HasExited; i++)
        {
            if (MasterIsAlive()) { started = true; break; }
            await Task.Delay(100);
        }
        if (!started && MasterIsAlive()) started = true;
        if (!started)
        {
            ProcessRunner.DeletePidFile(Id);
            try { if (File.Exists(MasterPidFile)) File.Delete(MasterPidFile); } catch { }
            throw new InvalidOperationException(
                "Nginx failed to start — check logs/nginx.log and logs/nginx-error.log" +
                ErrorLogTail() + Prereqs.Hint);
        }
    }

    private static bool MasterIsAlive()
    {
        try
        {
            return File.Exists(MasterPidFile) &&
                   int.TryParse(File.ReadAllText(MasterPidFile).Trim(), out var master) &&
                   ProcessRunner.IsAlive(master, "nginx");
        }
        catch { return false; }
    }

    private static string ErrorLogTail(int lines = 6)
    {
        try
        {
            var log = Path.Combine(Paths.Logs, "nginx-error.log");
            if (!File.Exists(log)) return "";
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, fs.Length - 16 * 1024), SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var all = new List<string>();
            while (sr.ReadLine() is { } line) if (line.Trim().Length > 0) all.Add(line);
            var tail = all.TakeLast(lines).ToList();
            return tail.Count == 0 ? "" : "\n\n" + string.Join("\n", tail);
        }
        catch { return ""; }
    }

    public override async Task StopAsync()
    {
        if (IsInstalled)
            await ProcessRunner.RunAsync(ExePath, $"-s stop {ConfArgs}", Paths.NginxDir, timeoutMs: 10_000);
        await Task.Delay(500);
        try
        {
            if (File.Exists(MasterPidFile) &&
                int.TryParse(File.ReadAllText(MasterPidFile).Trim(), out var master) &&
                ProcessRunner.IsAlive(master, "nginx"))
                ProcessRunner.KillPid(master);
        }
        catch { }
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
            if (ProcessRunner.IsAlive(pid, "nginx")) ProcessRunner.KillPid(pid);
        try { if (File.Exists(MasterPidFile)) File.Delete(MasterPidFile); } catch { }
        ProcessRunner.DeletePidFile(Id);
    }

    public override Task<string> GetInstalledVersionAsync()
        => ToolVersionCache.GetAsync(ExePath, async () =>
        {
            var (_, so, se) = await ProcessRunner.RunAsync(ExePath, "-v", Paths.NginxDir, timeoutMs: 10_000);
            var m = System.Text.RegularExpressions.Regex.Match(so + se, @"nginx/([\d.]+)");
            return m.Success ? m.Groups[1].Value : "";
        });

    public async Task ReloadAsync()
    {
        if (!IsRunning) return;
        var (code, so, se) = await ProcessRunner.RunAsync(
            ExePath, $"-s reload {ConfArgs}", Paths.NginxDir, timeoutMs: 10_000);
        if (code != 0)
            throw new InvalidOperationException(
                "Nginx rejected the new config and is still serving the previous one:\n" +
                (se + so).Trim() + ErrorLogTail());
    }

    private static string MasterPidFile => Path.Combine(Paths.Run, "nginx-master.pid");

    public override bool IsRunning =>
        MasterIsAlive() ||
        ProcessRunner.ReadPidFile(Id).Any(p => ProcessRunner.IsAlive(p, "nginx"));
}
