using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalPanel.Core;

/// <summary>
/// Makes the portable tools available in any terminal:
///  - bin/php/current and bin/node/current symlinks point at the selected versions
///  - bin/shims contains:
///      * php, php84/php83/... per installed version
///      * composer (runs on CURRENT php), composer83, art83, art (php artisan)
///      * node, npm, npx, corepack (run on CURRENT node)
///      * node18, node20, node22, npm18, npm20, npm22 (per-version NVM shims)
///      * nvm (CLI tool to switch versions, list installed versions, etc.)
///
/// On Unix the folder is NOT added to PATH — it would shadow the system and nvm
/// toolchains. Only the localpanel/lpanel CLI is exposed, via ~/.local/bin.
/// </summary>
public static class ShimManager
{
    public static void Apply(AppConfig config)
    {
        // --- Symlink / Junction for PHP ---
        if (config.DefaultPhpVersion is { } phpVer &&
            Directory.Exists(Path.Combine(Paths.PhpDir, phpVer)))
        {
            SetLink(Paths.PhpCurrent, Path.Combine(Paths.PhpDir, phpVer));
        }

        // --- Symlink / Junction for Node (NVM) ---
        var nodeVer = config.CurrentNodeVersion;
        if (nodeVer is null || !Directory.Exists(Path.Combine(Paths.NodeDir, nodeVer)))
            nodeVer = Paths.VersionDirs(Paths.NodeDir)
                .Select(Path.GetFileName).OrderByDescending(v => Pad(v!)).FirstOrDefault();
        if (nodeVer is not null)
        {
            SetLink(Paths.NodeCurrent, Path.Combine(Paths.NodeDir, nodeVer));
            if (config.CurrentNodeVersion != nodeVer) { config.CurrentNodeVersion = nodeVer; config.Save(); }
        }

        if (Platform.IsWindows)
        {
            ApplyWindowsShims(config);
        }
        else
        {
            ApplyUnixShims(config);
        }

        EnsureUserPath();
    }

    private static void ApplyWindowsShims(AppConfig config)
    {
        // PHP shims
        WriteCmd("php.cmd", $"@\"%~dp0..\\php\\current\\php.exe\" %*");
        foreach (var dir in Paths.VersionDirs(Paths.PhpDir))
        {
            var v = Path.GetFileName(dir);
            var parts = v.Split('.');
            if (parts.Length < 2) continue;
            var tag = $"{parts[0]}{parts[1]}";
            var exe = $"%~dp0..\\php\\{v}\\php.exe";
            WriteCmd($"php{tag}.cmd", $"@\"{exe}\" %*");
            if (File.Exists(Paths.ComposerPhar))
                WriteCmd($"composer{tag}.cmd", $"@\"{exe}\" \"%~dp0..\\composer\\composer.phar\" %*");
            WriteCmd($"art{tag}.cmd", $"@\"{exe}\" artisan %*");
        }
        WriteCmd("art.cmd", "@\"%~dp0..\\php\\current\\php.exe\" artisan %*");
        if (File.Exists(Paths.ComposerPhar))
            WriteCmd("composer.cmd", $"@\"%~dp0..\\php\\current\\php.exe\" \"%~dp0..\\composer\\composer.phar\" %*");

        // Node / NVM shims
        WriteCmd("node.cmd", "@\"%~dp0..\\node\\current\\node.exe\" %*");
        WriteCmd("npm.cmd", "@\"%~dp0..\\node\\current\\npm.cmd\" %*");
        WriteCmd("npx.cmd", "@\"%~dp0..\\node\\current\\npx.cmd\" %*");

        foreach (var dir in Paths.VersionDirs(Paths.NodeDir))
        {
            var v = Path.GetFileName(dir);
            var major = v.Split('.')[0];
            WriteCmd($"node{major}.cmd", $"@\"%~dp0..\\node\\{v}\\node.exe\" %*");
            WriteCmd($"npm{major}.cmd", $"@\"%~dp0..\\node\\{v}\\npm.cmd\" %*");
            WriteCmd($"npx{major}.cmd", $"@\"%~dp0..\\node\\{v}\\npx.cmd\" %*");
        }

        // nvm CLI helper
        WriteCmd("nvm.cmd", $"""
            @echo off
            if "%1"=="" goto list
            if "%1"=="list" goto list
            if "%1"=="ls" goto list
            if "%1"=="use" goto use
            echo Usage: nvm [list ^| use ^<version^>]
            exit /b 0
            :list
            echo Installed Node versions:
            for /d %%D in ("%~dp0..\node\*") do (
                if not "%%~nxD"=="current" echo   %%~nxD
            )
            exit /b 0
            :use
            if "%2"=="" (echo Specify version, e.g. nvm use 20 & exit /b 1)
            for /d %%D in ("%~dp0..\node\%2*") do (
                if exist "%%D\node.exe" (
                    rmdir "%~dp0..\node\current" 2>nul
                    mklink /J "%~dp0..\node\current" "%%D" >nul
                    echo Switched to Node %%~nxD
                    exit /b 0
                )
            )
            echo Node version %2 not found.
            exit /b 1
            """);
    }

    private static void ApplyUnixShims(AppConfig config)
    {
        // LocalPanel's own CLI, so "localpanel status" and the short "lpanel status" work
        // from any directory. Written only when the CLI has actually been built, otherwise
        // the commands would exist on PATH but fail. ("lpanel" rather than "lp", which is
        // the CUPS printing command and would be shadowed — the shims dir leads PATH.)
        var cli = Path.Combine(Paths.Root, "dist", Platform.Exe("localpanel"));
        if (File.Exists(cli))
        {
            foreach (var name in new[] { "localpanel", "lpanel" })
            {
                WriteBash(name, """
                    #!/usr/bin/env bash
                    DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                    exec "$DIR/../../dist/localpanel" "$@"
                    """);
            }
        }

        // PHP shims
        WriteBash("php", """
            #!/usr/bin/env bash
            DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
            exec "$DIR/../php/current/bin/php" "$@"
            """);

        foreach (var dir in Paths.VersionDirs(Paths.PhpDir))
        {
            var v = Path.GetFileName(dir);
            var parts = v.Split('.');
            if (parts.Length < 2) continue;
            var tag = $"{parts[0]}{parts[1]}";
            WriteBash($"php{tag}", $$"""
                #!/usr/bin/env bash
                DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                exec "$DIR/../php/{{v}}/bin/php" "$@"
                """);

            if (File.Exists(Paths.ComposerPhar))
            {
                WriteBash($"composer{tag}", $$"""
                    #!/usr/bin/env bash
                    DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                    exec "$DIR/../php/{{v}}/bin/php" "$DIR/../composer/composer.phar" "$@"
                    """);
            }

            WriteBash($"art{tag}", $$"""
                #!/usr/bin/env bash
                DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                exec "$DIR/../php/{{v}}/bin/php" artisan "$@"
                """);
        }

        WriteBash("art", """
            #!/usr/bin/env bash
            DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
            exec "$DIR/../php/current/bin/php" artisan "$@"
            """);

        if (File.Exists(Paths.ComposerPhar))
        {
            WriteBash("composer", """
                #!/usr/bin/env bash
                DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                exec "$DIR/../php/current/bin/php" "$DIR/../composer/composer.phar" "$@"
                """);
        }

        // Node / NVM global shims
        WriteBash("node", """
            #!/usr/bin/env bash
            DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
            exec "$DIR/../node/current/bin/node" "$@"
            """);
        WriteBash("npm", """
            #!/usr/bin/env bash
            DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
            exec "$DIR/../node/current/bin/npm" "$@"
            """);
        WriteBash("npx", """
            #!/usr/bin/env bash
            DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
            exec "$DIR/../node/current/bin/npx" "$@"
            """);

        // Per-version NVM shims (node18, node20, node22, npm18, npm20, npm22, npx20...)
        foreach (var dir in Paths.VersionDirs(Paths.NodeDir))
        {
            var v = Path.GetFileName(dir);
            var major = v.Split('.')[0];
            WriteBash($"node{major}", $$"""
                #!/usr/bin/env bash
                DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                exec "$DIR/../node/{{v}}/bin/node" "$@"
                """);
            WriteBash($"npm{major}", $$"""
                #!/usr/bin/env bash
                DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                exec "$DIR/../node/{{v}}/bin/npm" "$@"
                """);
            WriteBash($"npx{major}", $$"""
                #!/usr/bin/env bash
                DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                exec "$DIR/../node/{{v}}/bin/npx" "$@"
                """);
        }

        // nvm CLI helper
        WriteBash("nvm", """
            #!/usr/bin/env bash
            DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
            NODE_DIR="$DIR/../node"

            case "$1" in
                list|ls|"")
                    echo "LocalPanel Installed Node Versions:"
                    for d in "$NODE_DIR"/*/; do
                        name="$(basename "$d")"
                        if [ "$name" != "current" ] && [ -d "$d" ]; then
                            current_target="$(readlink "$NODE_DIR/current" 2>/dev/null | xargs basename 2>/dev/null)"
                            if [ "$name" = "$current_target" ]; then
                                echo "  * $name (active)"
                            else
                                echo "    $name"
                            fi
                        fi
                    done
                    ;;
                use)
                    ver="$2"
                    if [ -z "$ver" ]; then
                        echo "Usage: nvm use <version> (e.g. nvm use 20 or nvm use 20.20.2)"
                        exit 1
                    fi
                    target=""
                    for d in "$NODE_DIR"/$ver*/; do
                        if [ -f "$d/bin/node" ] || [ -f "$d/node" ]; then
                            target="$d"
                            break
                        fi
                    done
                    if [ -n "$target" ]; then
                        rm -f "$NODE_DIR/current"
                        ln -sf "$target" "$NODE_DIR/current"
                        echo "Now using Node $(basename "$target")"
                    else
                        echo "Node version matching '$ver' not found in LocalPanel."
                        exit 1
                    fi
                    ;;
                *)
                    echo "LocalPanel NVM CLI"
                    echo "Usage: nvm [list|ls]        - List installed Node versions"
                    echo "       nvm use <version>   - Switch active Node version"
                    ;;
            esac
            """);
    }

    private static void WriteCmd(string name, string body)
        => File.WriteAllText(Path.Combine(Paths.ShimsDir, name), body + Environment.NewLine);

    private static void WriteBash(string name, string body)
    {
        var file = Path.Combine(Paths.ShimsDir, name);
        File.WriteAllText(file, body.Trim() + "\n");
        try
        {
            File.SetUnixFileMode(file,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { }
    }

    private static string Pad(string version)
        => string.Join(".", version.Split('.').Select(p => p.PadLeft(6, '0')));

    private static void SetLink(string link, string target)
    {
        if (Directory.Exists(link) || File.Exists(link))
        {
            try
            {
                if (Platform.IsWindows)
                {
                    var attrs = File.GetAttributes(link);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        Directory.Delete(link, recursive: false);
                }
                else
                {
                    File.Delete(link);
                }
            }
            catch { }
        }

        if (Platform.IsWindows)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(15_000);
        }
        else
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch
            {
                var psi = new System.Diagnostics.ProcessStartInfo("ln", $"-sfn \"{target}\" \"{link}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(5_000);
            }
        }
    }

    public static void EnsureUserPath()
    {
        if (Platform.IsWindows)
        {
            EnsureUserPathWindows();
        }
        else
        {
            EnsureUserPathUnix();
        }
    }

    private static void EnsureUserPathWindows()
    {
        try
        {
            var wanted = new[] { Paths.ShimsDir, Paths.NodeCurrent };
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Environment");
            var current = key.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
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
            if (changed)
            {
                key.SetValue("Path", string.Join(';', parts), Microsoft.Win32.RegistryValueKind.ExpandString);
            }
        }
        catch { }
    }

    private static void EnsureUserPathUnix()
    {
        RemoveStaleUnixPathEntries();
        LinkUnixCliAliases();
    }

    /// <summary>
    /// Earlier builds appended "export PATH=&lt;shims&gt;:$PATH" to the shell rc files, which
    /// shadowed the user's own php/node. Those lines are ours, so take them back out.
    /// </summary>
    private static void RemoveStaleUnixPathEntries()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var rc in new[] { ".bashrc", ".zshrc", ".profile", ".bash_profile" })
            {
                var path = Path.Combine(home, rc);
                if (!File.Exists(path)) continue;

                var kept = File.ReadAllLines(path)
                    .Where(l => !l.Contains(Paths.ShimsDir, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (kept.Count == File.ReadAllLines(path).Length) continue;

                File.WriteAllText(path, string.Join(Environment.NewLine, kept).TrimEnd() + Environment.NewLine);
            }
        }
        catch { }
    }

    private static void LinkUnixCliAliases()
    {
        try
        {
            var cli = Path.Combine(Paths.Root, "dist", Platform.Exe("localpanel"));
            if (!File.Exists(cli)) return;

            var bin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
            Directory.CreateDirectory(bin);

            foreach (var name in new[] { "localpanel", "lpanel" })
            {
                var link = Path.Combine(bin, name);
                if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
                Directory.CreateSymbolicLink(link, cli);
            }
        }
        catch { }
    }
}
