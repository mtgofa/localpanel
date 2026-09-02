using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace LocalPanel.Core;

/// <summary>
/// Downloads release archives (zip, tar.gz, tar.xz) and extracts them into the portable bin folder,
/// flattening single top-level directories (nginx-1.27.4/, node-v20-linux-x64/, ...).
/// </summary>
public class Downloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        c.Timeout = TimeSpan.FromMinutes(30);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.9.1");
        return c;
    }

    /// <summary>
    /// Download to a sibling .part file and move it into place only when the
    /// transfer completed, so a dropped connection never leaves a truncated file behind.
    /// </summary>
    public static async Task<string> DownloadFileAsync(
        string url, string destFile, IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        var part = destFile + ".part";
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;

            long done = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
            {
                var buffer = new byte[1 << 16];
                long lastReported = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (done - lastReported >= (1 << 20))
                    {
                        lastReported = done;
                        progress?.Report((done, total));
                    }
                }
            }
            progress?.Report((done, total < 0 ? done : total));

            File.Move(part, destFile, overwrite: true);

            // On Linux/macOS, ensure downloaded binaries/scripts have execute permission.
            if (!Platform.IsWindows)
            {
                try
                {
                    File.SetUnixFileMode(destFile,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { }
            }

            return destFile;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>
    /// Download an archive (zip, tar.gz, tar.xz) and extract it into targetDir (created fresh).
    /// </summary>
    public static async Task DownloadAndExtractAsync(
        string url, string targetDir, IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default, Func<Task>? beforeSwap = null)
    {
        var ext = url.Contains(".tar.gz", StringComparison.OrdinalIgnoreCase) || url.Contains(".tgz", StringComparison.OrdinalIgnoreCase)
            ? ".tar.gz"
            : url.Contains(".tar.xz", StringComparison.OrdinalIgnoreCase)
                ? ".tar.xz"
                : ".zip";

        var tmpArchive = Path.Combine(Paths.Tmp, Guid.NewGuid().ToString("N") + ext);
        var tmpExtract = Path.Combine(Paths.Tmp, Guid.NewGuid().ToString("N"));
        try
        {
            await DownloadFileAsync(url, tmpArchive, progress, ct);

            var sourceDir = await Task.Run(() =>
            {
                Directory.CreateDirectory(tmpExtract);
                ExtractArchive(tmpArchive, tmpExtract);

                // Ensure execution permissions on extracted binaries on Unix
                if (!Platform.IsWindows)
                {
                    EnsureUnixExecutablePermissions(tmpExtract);
                }

                // Flatten: if the archive contains exactly one top-level folder, use its contents.
                var entries = Directory.GetFileSystemEntries(tmpExtract);
                return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : tmpExtract;
            }, ct);

            if (beforeSwap is not null) await beforeSwap();

            await Task.Run(() => SwapIntoPlace(sourceDir, targetDir), ct);
        }
        finally
        {
            TryDelete(tmpArchive);
            TryDeleteDir(tmpExtract);
        }
    }

    private static void ExtractArchive(string archivePath, string destinationDir)
    {
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var fileStream = File.OpenRead(archivePath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzipStream, destinationDir, overwriteFiles: true);
        }
        else if (archivePath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
        {
            // If tar CLI is available on Unix, extract .tar.xz using tar command, else standard fallback
            if (!Platform.IsWindows)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("tar", $"-xf \"{archivePath}\" -C \"{destinationDir}\"")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException("Failed to extract tar.xz archive");
            }
            else
            {
                throw new NotSupportedException(".tar.xz is not supported natively on Windows without tar installed.");
            }
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
        }
    }

    private static void EnsureUnixExecutablePermissions(string directory)
    {
        try
        {
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                // Executable binaries, scripts, or files in bin/
                var dir = Path.GetDirectoryName(file) ?? "";
                if (dir.EndsWith("/bin", StringComparison.OrdinalIgnoreCase) ||
                    dir.EndsWith("/sbin", StringComparison.OrdinalIgnoreCase) ||
                    !name.Contains('.') || name.EndsWith(".sh") || name.EndsWith(".phar"))
                {
                    try
                    {
                        var mode = File.GetUnixFileMode(file);
                        File.SetUnixFileMode(file, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void SwapIntoPlace(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        var old = targetDir + ".old";
        TryDeleteDir(old);
        var hadOld = Directory.Exists(targetDir);
        if (hadOld) Directory.Move(targetDir, old);
        try
        {
            try
            {
                Directory.Move(sourceDir, targetDir);
            }
            catch (IOException)
            {
                TryDeleteDir(targetDir);
                CopyDirectory(sourceDir, targetDir);
            }
        }
        catch
        {
            TryDeleteDir(targetDir);
            if (hadOld && Directory.Exists(old) && !Directory.Exists(targetDir))
            {
                try { Directory.Move(old, targetDir); } catch { }
            }
            throw;
        }
        if (hadOld) TryDeleteDir(old);
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            if (!Platform.IsWindows)
            {
                try { File.SetUnixFileMode(dest, File.GetUnixFileMode(file)); } catch { }
            }
        }
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        return await Http.GetStringAsync(url, cts.Token);
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    private static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
