using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

/// <summary>
/// Mailpit: catches every email local sites send (SMTP on 1025 by default)
/// and serves an inbox web UI.
/// </summary>
public class MailpitService : ManagedService
{
    public MailpitService(AppConfig config) : base(config) { }

    public override string Id => "mailpit";
    public override string DisplayName => "Mailpit";
    public string ExePath => Paths.MailpitExe;
    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => $"{Config.Mailpit.SmtpPort} smtp / {Config.Mailpit.UiPort} web";

    public override bool AutoStart
    {
        get => Config.Mailpit.AutoStart;
        set { Config.Mailpit.AutoStart = value; Config.Save(); }
    }

    public string UiUrl => $"http://127.0.0.1:{Config.Mailpit.UiPort}";

    public override Task<string> GetInstalledVersionAsync()
        => ToolVersionCache.GetAsync(ExePath, async () =>
        {
            var (_, so, se) = await ProcessRunner.RunAsync(ExePath, "version", timeoutMs: 10_000);
            var m = System.Text.RegularExpressions.Regex.Match(so + se, @"v?(\d+\.\d+[\d.]*)");
            return m.Success ? m.Groups[1].Value : "";
        });

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("Mailpit is not installed yet.");
        if (IsRunning) return;
        var db = Path.Combine(Paths.Ensure(Path.Combine(Paths.Data, "mailpit")), "mailpit.db");
        var p = ProcessRunner.StartBackground(
            ExePath,
            $"--listen 127.0.0.1:{Config.Mailpit.UiPort} --smtp 127.0.0.1:{Config.Mailpit.SmtpPort} --db-file \"{db}\"",
            Paths.MailpitDir, logFile: LogFile);
        ProcessRunner.WritePidFile(Id, p.Id);
        await Task.Delay(600);
        if (p.HasExited)
        {
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException("Mailpit exited immediately — check logs/mailpit.log");
        }
    }

    public override Task StopAsync()
    {
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
            if (ProcessRunner.IsAlive(pid, "mailpit")) ProcessRunner.KillPid(pid);
        ProcessRunner.DeletePidFile(Id);
        return Task.CompletedTask;
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Any(p => ProcessRunner.IsAlive(p, "mailpit"));
        }
    }
}
