using System.Diagnostics;
using System.IO;
using System.Text;

namespace LocalPanel.Core;

/// <summary>
/// Owns a clearly-labeled block inside the system hosts file; everything
/// outside the markers is left untouched.
/// </summary>
public static class HostsFileManager
{
    private static readonly Encoding HostsEncoding = Encoding.UTF8;

    private const string Begin = "# ========== LocalPanel BEGIN (managed by LocalPanel - do not edit this block) ==========";
    private const string End = "# ========== LocalPanel END ==========";

    public static string HostsPath => Platform.IsWindows
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
        : "/etc/hosts";

    public static void SyncSites(IEnumerable<(string Domain, string Name)> sites)
    {
        var entries = sites
            .DistinctBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.Domain)
            .ToList();
        var ourDomains = entries.Select(s => s.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = ReadHosts();
        var original = lines.ToArray();

        RemoveBlock(lines, Begin, End);
        RemoveBlock(lines, "# Herd generated Hosts. Do not change.", "# End Herd generated Hosts");
        lines.RemoveAll(l =>
        {
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) return false;
            var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && ourDomains.Contains(parts[1]);
        });

        if (entries.Count > 0)
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

        if (lines.SequenceEqual(original, StringComparer.Ordinal)) return;

        try
        {
            File.WriteAllLines(HostsPath, lines, HostsEncoding);
        }
        catch (UnauthorizedAccessException)
        {
            if (Platform.IsWindows)
            {
                GrantWriteOnceWindows();
                File.WriteAllLines(HostsPath, lines, HostsEncoding);
            }
            else
            {
                WriteWithPkexec(lines);
            }
        }
    }

    private static void WriteWithPkexec(List<string> lines)
    {
        var tmp = Path.Combine(Paths.Tmp, "hosts.new");
        File.WriteAllLines(tmp, lines, HostsEncoding);
        try
        {
            // Use pkexec or sudo to copy the new hosts file over /etc/hosts
            var psi = new ProcessStartInfo("pkexec", $"cp \"{tmp}\" \"{HostsPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                p.WaitForExit(30_000);
                if (p.ExitCode != 0)
                {
                    var err = p.StandardError.ReadToEnd();
                    throw new InvalidOperationException($"Failed to update /etc/hosts via pkexec: {err}");
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static void GrantWriteOnceWindows()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new InvalidOperationException("Could not determine the current user.");
        var psi = new ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = $"\"{HostsPath}\" /grant *{sid}:M",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(
                "The permission prompt was cancelled. LocalPanel needs one-time permission " +
                "to edit the hosts file so .test domains resolve.");
        }
        using (p)
        {
            if (p is null) throw new InvalidOperationException("Could not request hosts-file permission.");
            if (!p.WaitForExit(120_000) || p.ExitCode != 0)
                throw new InvalidOperationException(
                    "Hosts-file permission was not granted - sites cannot be registered without it.");
        }
    }

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
