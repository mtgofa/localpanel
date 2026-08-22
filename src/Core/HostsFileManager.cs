using System.IO;
using System.Text;

namespace LocalPanel.Core;

/// <summary>
/// Owns a clearly-labeled block inside the Windows hosts file; everything
/// outside the markers is left untouched. Requires the app to run elevated.
/// </summary>
public static class HostsFileManager
{
    // Latin1 round-trips foreign bytes 1:1 (every byte maps to exactly one char and
    // back), so UTF-8/ANSI content other tools wrote outside our block is preserved
    // byte-for-byte; our own markers and block stay 7-bit ASCII so detection is unaffected.
    private static readonly Encoding HostsEncoding = Encoding.Latin1;

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


        var lines = ReadHosts();
        // Snapshot BEFORE mutating so an unchanged file is never rewritten (this runs
        // on every launch; rewriting would also re-trigger the UAC grant needlessly).
        var original = lines.ToArray();

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

        if (entries.Count > 0) // no block at all when there are no sites
        {
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
        }

        if (lines.SequenceEqual(original, StringComparer.Ordinal)) return; // already current

        try
        {
            File.WriteAllLines(HostsPath, lines, HostsEncoding);
        }
        catch (UnauthorizedAccessException)
        {
            // One-time: grant the current user modify rights on the hosts file
            // (single UAC prompt, then the app never needs elevation again).
            GrantWriteOnce();
            File.WriteAllLines(HostsPath, lines, HostsEncoding);
        }
    }

    private static void GrantWriteOnce()
    {
        // SID form (*S-1-5-...) resolves for local, domain, MSA and Azure AD accounts
        // alike; a bare Environment.UserName fails on AzureAD-joined machines.
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new InvalidOperationException("Could not determine the current user.");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = $"\"{HostsPath}\" /grant *{sid}:M",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        System.Diagnostics.Process? p;
        try
        {
            p = System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            throw new InvalidOperationException(
                "The permission prompt was cancelled. LocalPanel needs one-time permission " +
                "to edit the hosts file so .test domains resolve - save the site again and accept the prompt.");
        }
        using (p)
        {
            if (p is null) throw new InvalidOperationException("Could not request hosts-file permission.");
            if (!p.WaitForExit(120_000) || p.ExitCode != 0)
                throw new InvalidOperationException(
                    "Hosts-file permission was not granted - sites cannot be registered without it.");
        }
    }

    /// <summary>Byte-transparent read: no BOM sniffing, so a UTF-8-with-BOM file written by
    /// another tool is round-tripped unchanged (the BOM survives as Latin1 chars).</summary>
    private static List<string> ReadHosts()
    {
        var list = new List<string>();
        if (!File.Exists(HostsPath)) return list;
        using var sr = new StreamReader(HostsPath, HostsEncoding, detectEncodingFromByteOrderMarks: false);
        while (sr.ReadLine() is { } line) list.Add(line);
        return list;
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
