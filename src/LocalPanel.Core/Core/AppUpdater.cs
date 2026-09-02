using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LocalPanel.Core;

/// <summary>
/// Self-update from GitHub Releases: checks {UpdateRepo}/releases/latest,
/// downloads the newer binary next to the current one, then swaps it in after exit and relaunches.
/// </summary>
public static class AppUpdater
{
    public const string UpdateRepo = "mtgofa/localpanel";

    private static string PendingExe => Path.Combine(Paths.Root, Platform.IsWindows ? "LocalPanel.new.exe" : "localpanel.new");

    public static async Task<string?> CheckAndDownloadAsync()
    {
        try
        {
            var json = await Downloader.GetStringAsync($"https://api.github.com/repos/{UpdateRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            if (tag is null || !Version.TryParse(tag, out var latest)) return null;

            var cur = typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            if (latest <= new Version(cur.Major, cur.Minor, cur.Build)) return null;

            // A release carries both the CLI and the GUI, so pick the asset belonging to
            // *this* executable — matching on "contains linux" alone would happily hand
            // localpanel-gui-linux-x64 to the running localpanel CLI. Releases that ship a
            // single generic binary still work through the fallback.
            var procName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "localpanel";
            string? url = null;
            string? fallback = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var link = asset.GetProperty("browser_download_url").GetString();

                if (Platform.IsWindows)
                {
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(Path.GetFileNameWithoutExtension(name), procName,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        url = link;
                        break;
                    }
                }
                else
                {
                    if (!name.Contains("linux", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith(procName + "-linux", StringComparison.OrdinalIgnoreCase))
                    {
                        url = link;
                        break;
                    }
                }

                fallback ??= link;
            }
            url ??= fallback;
            if (url is null) return null;

            await Downloader.DownloadFileAsync(url, PendingExe);
            return tag;
        }
        catch { return null; }
    }

    public static void ApplyDownloadedUpdate()
    {
        if (!File.Exists(PendingExe)) return;
        var exe = Environment.ProcessPath!;
        var pid = Environment.ProcessId;

        if (Platform.IsWindows)
        {
            var script = Path.Combine(Paths.Tmp, "update.cmd");
            var name = Path.GetFileName(exe);
            File.WriteAllText(script, $"""
                @echo off
                :wait
                tasklist /FI "PID eq {pid}" | find /I "{name}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
                set /a n=0
                :move
                move /Y "{PendingExe}" "{exe}" >nul 2>&1 && goto run
                set /a n+=1
                if %n% lss 10 (timeout /t 1 /nobreak >nul & goto move)
                :run
                start "" "{exe}"
                del "%~f0"
                """);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        else
        {
            var script = Path.Combine(Paths.Tmp, "update.sh");
            File.WriteAllText(script, $"""
                #!/usr/bin/env bash
                while kill -0 {pid} 2>/dev/null; do
                    sleep 0.5
                done
                mv -f "{PendingExe}" "{exe}"
                chmod +x "{exe}"
                nohup "{exe}" >/dev/null 2>&1 &
                rm -f "$0"
                """);
            try
            {
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { }

            Process.Start(new ProcessStartInfo("bash", $"\"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
    }
}
