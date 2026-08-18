using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LocalPanel.Core;

/// <summary>
/// Makes the portable tools available in any terminal:
///  - bin\php\current and bin\node\current junctions point at the selected versions
///  - bin\shims contains php.cmd, php84.cmd/php83.cmd/... per installed version,
///    and composer.cmd (always runs on the CURRENT php)
///  - the shims folder and node\current are added to the user PATH once
/// Switching a version only repoints a junction — open terminals pick it up immediately.
/// </summary>
public static class ShimManager
{
    public static void Apply(AppConfig config)
    {
        // --- junctions ---
        if (config.DefaultPhpVersion is { } phpVer &&
            Directory.Exists(Path.Combine(Paths.PhpDir, phpVer)))
            SetJunction(Paths.PhpCurrent, Path.Combine(Paths.PhpDir, phpVer));

        var nodeVer = config.CurrentNodeVersion;
        if (nodeVer is null || !Directory.Exists(Path.Combine(Paths.NodeDir, nodeVer)))
            nodeVer = Paths.VersionDirs(Paths.NodeDir)
                .Select(Path.GetFileName).OrderByDescending(v => Pad(v!)).FirstOrDefault();
        if (nodeVer is not null)
        {
            SetJunction(Paths.NodeCurrent, Path.Combine(Paths.NodeDir, nodeVer));
            if (config.CurrentNodeVersion != nodeVer) { config.CurrentNodeVersion = nodeVer; config.Save(); }
        }

        // --- shims ---
        WriteCmd("php.cmd", $"@\"%~dp0..\\php\\current\\php.exe\" %*");
        foreach (var dir in Paths.VersionDirs(Paths.PhpDir))
        {
            var v = Path.GetFileName(dir);
            var parts = v.Split('.');
            if (parts.Length < 2) continue;
            var shim = $"php{parts[0]}{parts[1]}"; // 8.3.33 -> php83
            WriteCmd(shim + ".cmd", $"@\"%~dp0..\\php\\{v}\\php.exe\" %*");
        }
        if (File.Exists(Paths.ComposerPhar))
            WriteCmd("composer.cmd",
                $"@\"%~dp0..\\php\\current\\php.exe\" \"%~dp0..\\composer\\composer.phar\" %*");

        EnsureUserPath();
    }

    private static void WriteCmd(string name, string body)
        => File.WriteAllText(Path.Combine(Paths.ShimsDir, name), body + Environment.NewLine);

    private static string Pad(string version)
        => string.Join(".", version.Split('.').Select(p => p.PadLeft(6, '0')));

    // --- junction handling (mklink /J needs no special privileges) ---

    private static void SetJunction(string link, string target)
    {
        if (Directory.Exists(link))
        {
            var attrs = File.GetAttributes(link);
            if (!attrs.HasFlag(FileAttributes.ReparsePoint))
                return; // a real folder is squatting on the name; leave it alone
            Directory.Delete(link, recursive: false);
        }
        // Fully synchronous on purpose: this can be called from the UI thread,
        // and blocking on an async task there deadlocks WPF.
        var psi = new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"mklink timed out for {link}");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Could not create junction {link} -> {target}: {err}");
    }

    // --- PATH (user scope) ---

    public static void EnsureUserPath()
    {
        var wanted = new[] { Paths.ShimsDir, Paths.NodeCurrent };
        using var key = Registry.CurrentUser.CreateSubKey("Environment");
        var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var changed = false;
        foreach (var w in wanted)
        {
            if (!parts.Any(p => string.Equals(p.Trim(), w, StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(w);
                changed = true;
            }
        }
        if (!changed) return;
        key.SetValue("Path", string.Join(';', parts), RegistryValueKind.ExpandString);
        BroadcastEnvironmentChange();
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam,
        string lParam, uint flags, uint timeout, out UIntPtr result);

    private static void BroadcastEnvironmentChange()
    {
        // HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
        _ = SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }
}
