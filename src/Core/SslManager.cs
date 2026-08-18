using System.IO;

namespace LocalPanel.Core;

/// <summary>
/// Wraps mkcert: installs the local CA into the Windows trust store once,
/// then issues a certificate per site domain into etc\ssl.
/// </summary>
public static class SslManager
{
    public static bool IsInstalled => File.Exists(Paths.MkcertExe);

    private static readonly Dictionary<string, string> Env = new()
    {
        // Keep the CA inside the portable root too.
        ["CAROOT"] = Paths.Ensure(Path.Combine(Paths.Etc, "ca")),
    };

    public static async Task EnsureCaInstalledAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("mkcert is not installed yet.");
        var (code, _, err) = await ProcessRunner.RunAsync(Paths.MkcertExe, "-install", env: Env, timeoutMs: 60_000);
        if (code != 0) throw new InvalidOperationException("mkcert -install failed: " + err);
        EnableFirefoxTrust();
    }

    /// <summary>
    /// Firefox keeps its own trust store; flipping security.enterprise_roots
    /// makes it also trust the Windows store (where the mkcert CA lives).
    /// </summary>
    public static void EnableFirefoxTrust()
    {
        var profiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(profiles)) return; // Firefox not installed
        const string pref = "user_pref(\"security.enterprise_roots.enabled\", true);";
        foreach (var profile in Directory.GetDirectories(profiles))
        {
            try
            {
                var userJs = Path.Combine(profile, "user.js");
                var existing = File.Exists(userJs) ? File.ReadAllText(userJs) : "";
                if (existing.Contains("security.enterprise_roots.enabled")) continue;
                File.AppendAllText(userJs,
                    (existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : "") +
                    pref + Environment.NewLine);
            }
            catch { /* a locked profile shouldn't break cert setup */ }
        }
    }

    public static async Task IssueCertificateAsync(string domain, IEnumerable<string>? aliases = null)
    {
        if (!IsInstalled) throw new InvalidOperationException("mkcert is not installed yet.");
        var cert = Path.Combine(Paths.SslDir, domain + ".pem");
        var key = Path.Combine(Paths.SslDir, domain + "-key.pem");
        if (File.Exists(cert) && File.Exists(key)) return;

        var names = string.Join(' ', new[] { domain }.Concat(aliases ?? Enumerable.Empty<string>()));
        var (code, _, err) = await ProcessRunner.RunAsync(
            Paths.MkcertExe,
            $"-cert-file \"{cert}\" -key-file \"{key}\" {names}",
            env: Env, timeoutMs: 60_000);
        if (code != 0) throw new InvalidOperationException($"mkcert failed for {domain}: {err}");
    }

    /// <summary>Delete a site's cert so the next apply reissues it (e.g. after alias changes).</summary>
    public static void InvalidateCertificate(string domain)
    {
        foreach (var f in new[]
                 {
                     Path.Combine(Paths.SslDir, domain + ".pem"),
                     Path.Combine(Paths.SslDir, domain + "-key.pem"),
                 })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }
}
