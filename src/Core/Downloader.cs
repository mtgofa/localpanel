using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace LocalPanel.Core;

/// <summary>
/// Downloads release archives and extracts them into the portable bin folder,
/// flattening single top-level directories (nginx-1.27.4/, node-v20-win-x64/, ...).
/// </summary>
public class Downloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        c.Timeout = TimeSpan.FromMinutes(30);
        // Oracle's CDN (dev.mysql.com) 403s browser-like and unknown UAs but
        // allows CLI tools; every other source we use accepts this UA too.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.9.1");
        return c;
    }

    /// <summary>
    /// Download to a sibling .part file and move it into place only when the
    /// transfer completed, so a dropped connection never leaves a truncated
    /// composer.phar / mkcert.exe / LocalPanel.new.exe behind.
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
                // HttpClient fails the read on a Content-Length mismatch, so a short
                // transfer throws here and never reaches the move below.
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    // Throttle: every 1 MB — each report is a dispatcher hop.
                    if (done - lastReported >= (1 << 20))
                    {
                        lastReported = done;
                        progress?.Report((done, total));
                    }
                }
            }
            progress?.Report((done, total < 0 ? done : total));

            // The live file is only touched once the download is complete.
            File.Move(part, destFile, overwrite: true);
            return destFile;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>
    /// Download a zip and extract it into targetDir (created fresh).
    /// The previous tree is kept until the new one is fully staged: the swap is
    /// rename-old → move-new → delete-old, and the old tree is restored if the
    /// move fails, so a locked dll or a failed download never leaves a
    /// half-deleted install. <paramref name="beforeSwap"/> (e.g. "stop the
    /// service") is invoked right before the swap, so the service keeps
    /// running through the (long) download and is down only for the swap.
    /// </summary>
    public static async Task DownloadAndExtractAsync(
        string url, string targetDir, IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default, Func<Task>? beforeSwap = null)
    {
        var tmpZip = Path.Combine(Paths.Tmp, Guid.NewGuid().ToString("N") + ".zip");
        var tmpExtract = Path.Combine(Paths.Tmp, Guid.NewGuid().ToString("N"));
        try
        {
            await DownloadFileAsync(url, tmpZip, progress, ct);

            // Everything below is blocking file I/O (tens of seconds for MySQL/MongoDB):
            // keep it off the captured UI SynchronizationContext.
            var sourceDir = await Task.Run(() =>
            {
                Directory.CreateDirectory(tmpExtract);
                ZipFile.ExtractToDirectory(tmpZip, tmpExtract);

                // Flatten: if the archive contains exactly one top-level folder, use its contents.
                var entries = Directory.GetFileSystemEntries(tmpExtract);
                return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : tmpExtract;
            }, ct);

            if (beforeSwap is not null) await beforeSwap();

            await Task.Run(() => SwapIntoPlace(sourceDir, targetDir), ct);
        }
        finally
        {
            TryDelete(tmpZip);
            TryDeleteDir(tmpExtract);
        }
    }

    /// <summary>Rename-swap: target → target.old, source → target, delete target.old.
    /// On failure the old tree is put back so the installed component survives.</summary>
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
                // Move across volumes/locked handles: fall back to copy.
                TryDeleteDir(targetDir);
                CopyDirectory(sourceDir, targetDir);
            }
        }
        catch
        {
            // Roll back: drop whatever landed and restore the previous install.
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
        }
    }

    /// <summary>Small metadata fetches (version catalogs) get a short timeout so an
    /// unreachable host degrades to the built-in fallback lists quickly.</summary>
    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        return await Http.GetStringAsync(url, cts.Token);
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    private static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
