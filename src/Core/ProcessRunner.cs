using System.Diagnostics;
using System.IO;
using System.Text;

namespace LocalPanel.Core;

public static class ProcessRunner
{
    /// <summary>Start a detached background process with output appended to a log file.</summary>
    public static Process StartBackground(
        string exe, string args, string? workingDir = null,
        IDictionary<string, string>? env = null, string? logFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(exe) ?? Paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = logFile is not null,
            RedirectStandardError = logFile is not null,
        };
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        var p = new Process { StartInfo = psi };
        if (logFile is not null)
        {
            var sync = new object();
            void Append(string? line)
            {
                if (line is null) return;
                lock (sync)
                {
                    try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { }
                }
            }
            p.OutputDataReceived += (_, e) => Append(e.Data);
            p.ErrorDataReceived += (_, e) => Append(e.Data);
        }
        p.Start();
        if (logFile is not null)
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        return p;
    }

    /// <summary>Run a process to completion and capture its output.</summary>
    public static async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        string exe, string args, string? workingDir = null,
        IDictionary<string, string>? env = null, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(exe) ?? Paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, so.ToString(), se + Environment.NewLine + "[timeout]");
        }
        return (p.ExitCode, so.ToString(), se.ToString());
    }

    public static void WritePidFile(string name, params int[] pids)
        => File.WriteAllLines(Path.Combine(Paths.Run, name + ".pid"), pids.Select(p => p.ToString()));

    public static int[] ReadPidFile(string name)
    {
        var f = Path.Combine(Paths.Run, name + ".pid");
        if (!File.Exists(f)) return Array.Empty<int>();
        return File.ReadAllLines(f)
            .Select(l => int.TryParse(l.Trim(), out var pid) ? pid : 0)
            .Where(p => p > 0).ToArray();
    }

    public static void DeletePidFile(string name)
    {
        var f = Path.Combine(Paths.Run, name + ".pid");
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    public static bool IsAlive(int pid, string? expectedExeName = null)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (expectedExeName is not null &&
                !p.ProcessName.Equals(expectedExeName, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch { return false; }
    }

    public static void KillPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
