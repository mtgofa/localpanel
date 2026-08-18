using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

public class NginxService : ManagedService
{
    public NginxService(AppConfig config) : base(config) { }

    public override string Id => "nginx";
    public override string DisplayName => "Nginx";
    public string ExePath => Path.Combine(Paths.NginxDir, "nginx.exe");
    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => $"{Config.Nginx.HttpPort} / {Config.Nginx.HttpsPort} (ssl)";

    public override bool AutoStart
    {
        get => Config.Nginx.AutoStart;
        set { Config.Nginx.AutoStart = value; Config.Save(); }
    }

    // nginx runs with -p (prefix) pointing at our etc\nginx folder so every
    // path in the generated config stays inside the portable root.
    // Forward slashes on purpose: a trailing backslash before the closing
    // quote escapes it in Windows argv parsing and mangles the whole command.
    private string Prefix => Paths.NginxEtc.Replace('\\', '/');
    private string ConfArgs =>
        $"-p \"{Prefix}/\" -c \"{Path.Combine(Paths.NginxEtc, "nginx.conf").Replace('\\', '/')}\"";

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("Nginx is not installed yet.");
        if (IsRunning) return;

        // nginx opens <prefix>/logs/error.log before reading the config.
        Paths.Ensure(Path.Combine(Paths.NginxEtc, "logs"));
        NginxConfigGenerator.WriteAll(Config);

        var p = ProcessRunner.StartBackground(ExePath, ConfArgs, Paths.NginxDir, logFile: LogFile);
        await Task.Delay(700);
        // nginx.exe forks a master + workers; the launcher process may exit or stay.
        var pids = System.Diagnostics.Process.GetProcessesByName("nginx")
            .Select(x => x.Id).ToArray();
        if (pids.Length == 0)
            throw new InvalidOperationException(
                "Nginx failed to start — check logs/nginx.log and logs/nginx-error.log" + Prereqs.Hint);
        ProcessRunner.WritePidFile(Id, pids);
    }

    public override async Task StopAsync()
    {
        if (IsInstalled)
            await ProcessRunner.RunAsync(ExePath, $"-s stop {ConfArgs}", Paths.NginxDir, timeoutMs: 10_000);
        await Task.Delay(500);
        // Belt and braces: kill anything left.
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
            if (ProcessRunner.IsAlive(pid, "nginx")) ProcessRunner.KillPid(pid);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("nginx"))
        {
            if (p.MainModule?.FileName?.StartsWith(Paths.Root, StringComparison.OrdinalIgnoreCase) == true)
                ProcessRunner.KillPid(p.Id);
            p.Dispose();
        }
        ProcessRunner.DeletePidFile(Id);
    }

    public async Task ReloadAsync()
    {
        NginxConfigGenerator.WriteAll(Config);
        if (IsRunning)
            await ProcessRunner.RunAsync(ExePath, $"-s reload {ConfArgs}", Paths.NginxDir, timeoutMs: 10_000);
    }

    public override bool IsRunning
    {
        get
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("nginx"))
            {
                try
                {
                    if (p.MainModule?.FileName?.StartsWith(Paths.Root, StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                }
                catch { /* access denied to other users' processes */ }
                finally { p.Dispose(); }
            }
            return false;
        }
    }
}
