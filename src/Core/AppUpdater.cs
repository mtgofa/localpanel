using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LocalPanel.Core;

/// <summary>
/// Self-update from GitHub Releases: checks {UpdateRepo}/releases/latest,
/// downloads the newer exe next to the current one, then a tiny cmd script
/// swaps it in after the app exits and relaunches.
/// </summary>
public static class AppUpdater
{
    /// <summary>Fixed release source — not configurable from inside the app.</summary>
    public const string UpdateRepo = "mtgofa/localpanel";

    private static string PendingExe => Path.Combine(Paths.Root, "LocalPanel.new.exe");

    /// <summary>Returns the new version tag when an update was downloaded, else null.</summary>
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

            string? url = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (url is null) return null;

            await Downloader.DownloadFileAsync(url, PendingExe);
            return tag;
        }
        catch { return null; }
    }

    /// <summary>Spawn the swap script (waits for our exit, replaces the exe, relaunches).</summary>
    public static void ApplyDownloadedUpdate()
    {
        if (!File.Exists(PendingExe)) return;
        var exe = Environment.ProcessPath!;
        // Wait on OUR pid + actual file name (the exe may be renamed, and another
        // portable copy's LocalPanel.exe must not keep us waiting), then retry the
        // move a few times — the file can stay locked briefly after the process exits.
        var name = Path.GetFileName(exe);
        var pid = Environment.ProcessId;
        var script = Path.Combine(Paths.Tmp, "update.cmd");
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
}
