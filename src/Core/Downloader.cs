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

    public static async Task<string> DownloadFileAsync(
        string url, string destFile, IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destFile);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Report((done, total));
        }
        return destFile;
    }

    /// <summary>Download a zip and extract it into targetDir (created fresh).</summary>
    public static async Task DownloadAndExtractAsync(
        string url, string targetDir, IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        var tmpZip = Path.Combine(Paths.Tmp, Guid.NewGuid().ToString("N") + ".zip");
        var tmpExtract = Path.Combine(Paths.Tmp, Guid.NewGuid().ToString("N"));
        try
        {
            await DownloadFileAsync(url, tmpZip, progress, ct);
            Directory.CreateDirectory(tmpExtract);
            ZipFile.ExtractToDirectory(tmpZip, tmpExtract);

            // Flatten: if the archive contains exactly one top-level folder, use its contents.
            var entries = Directory.GetFileSystemEntries(tmpExtract);
            var sourceDir = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : tmpExtract;

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            try
            {
                Directory.Move(sourceDir, targetDir);
            }
            catch (IOException)
            {
                // Move across volumes/locked handles: fall back to copy.
                CopyDirectory(sourceDir, targetDir);
            }
        }
        finally
        {
            TryDelete(tmpZip);
            TryDeleteDir(tmpExtract);
        }
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
