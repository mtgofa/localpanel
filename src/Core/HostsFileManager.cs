using System.IO;
using System.Text;

namespace LocalPanel.Core;

/// <summary>
/// Owns a clearly-labeled block inside the Windows hosts file; everything
/// outside the markers is left untouched. Requires the app to run elevated.
/// </summary>
public static class HostsFileManager
{
    // ASCII only — the hosts file is written as ASCII and a mangled marker
    // would break block detection on the next sync.
    private const string Begin = "# ========== LocalPanel BEGIN (managed by LocalPanel - do not edit this block) ==========";
    private const string End = "# ========== LocalPanel END ==========";

    public static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static void SyncSites(IEnumerable<(string Domain, string Name)> sites)
    {
        var entries = sites
            .DistinctBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.Domain)
            .ToList();
        var ourDomains = entries.Select(s => s.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = File.Exists(HostsPath)
            ? File.ReadAllLines(HostsPath).ToList()
            : new List<string>();

        RemoveBlock(lines, Begin, End);
        // Old Herd entries duplicate our domains — the panel owns them now.
        RemoveBlock(lines, "# Herd generated Hosts. Do not change.", "# End Herd generated Hosts");
        // Drop any stray line (laragon leftovers etc.) for a domain we manage,
        // so every site appears exactly once.
        lines.RemoveAll(l =>
        {
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) return false;
            var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && ourDomains.Contains(parts[1]);
        });

        var block = new List<string> { Begin };
        foreach (var (domain, name) in entries)
        {
            var label = Ascii(string.IsNullOrWhiteSpace(name) ? domain : name);
            block.Add($"127.0.0.1 {domain} # LocalPanel: {label}");
        }
        block.Add(End);

        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0) lines.Add("");
        lines.AddRange(block);

        try
        {
            File.WriteAllLines(HostsPath, lines, Encoding.ASCII);
        }
        catch (UnauthorizedAccessException)
        {
            // One-time: grant the current user modify rights on the hosts file
            // (single UAC prompt, then the app never needs elevation again).
            GrantWriteOnce();
            File.WriteAllLines(HostsPath, lines, Encoding.ASCII);
        }
    }

    private static void GrantWriteOnce()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = $"\"{HostsPath}\" /grant \"{Environment.UserName}\":M",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not request hosts-file permission.");
        if (!p.WaitForExit(120_000) || p.ExitCode != 0)
            throw new InvalidOperationException(
                "Hosts-file permission was not granted - sites cannot be registered without it.");
    }

    private static void RemoveBlock(List<string> lines, string begin, string end)
    {
        var b = lines.FindIndex(l => l.Trim() == begin);
        var e = lines.FindIndex(l => l.Trim() == end);
        if (b >= 0 && e > b) lines.RemoveRange(b, e - b + 1);
    }

    private static string Ascii(string s)
    {
        var filtered = new string(s.Where(ch => ch >= 32 && ch < 127).ToArray()).Trim();
        return filtered.Length > 0 ? filtered : "site";
    }
}
