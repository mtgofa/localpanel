using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace LocalPanel.Core;

/// <summary>
/// Wraps mkcert: installs the local CA into the trust store once,
/// then issues a certificate per site domain into etc/ssl.
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
    /// makes it also trust the OS store.
    /// </summary>
    public static void EnableFirefoxTrust()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var profiles = Platform.IsWindows
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles")
                : Path.Combine(home, ".mozilla", "firefox");

            if (!Directory.Exists(profiles)) return;
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
                catch { }
            }
        }
        catch { }
    }

    public static async Task IssueCertificateAsync(string domain, IEnumerable<string>? aliases = null)
    {
        if (!IsInstalled) throw new InvalidOperationException("mkcert is not installed yet.");
        var cert = Path.Combine(Paths.SslDir, domain + ".pem");
        var key = Path.Combine(Paths.SslDir, domain + "-key.pem");
        if (File.Exists(cert) && File.Exists(key) && CertIsCurrent(cert, domain, aliases)) return;

        var names = string.Join(' ', new[] { domain }.Concat(aliases ?? Enumerable.Empty<string>()));
        var (code, _, err) = await ProcessRunner.RunAsync(
            Paths.MkcertExe,
            $"-cert-file \"{cert}\" -key-file \"{key}\" {names}",
            env: Env, timeoutMs: 60_000);
        if (code != 0) throw new InvalidOperationException($"mkcert failed for {domain}: {err}");
    }

    private static bool CertIsCurrent(string certPath, string domain, IEnumerable<string>? aliases)
    {
        try
        {
            var col = new X509Certificate2Collection();
            col.ImportFromPemFile(certPath);
            if (col.Count == 0) return false;
            using var x = col[0];
            if (x.NotAfter <= DateTime.Now.AddDays(30)) return false;

            var wanted = new[] { domain }.Concat(aliases ?? Enumerable.Empty<string>())
                .Select(n => n.Trim().ToLowerInvariant())
                .Where(n => n.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ext in x.Extensions)
            {
                if (ext.Oid?.Value != "2.5.29.17") continue;
                var san = ext as X509SubjectAlternativeNameExtension
                          ?? new X509SubjectAlternativeNameExtension(ext.RawData);
                foreach (var dns in san.EnumerateDnsNames()) have.Add(dns.ToLowerInvariant());
                foreach (var ip in san.EnumerateIPAddresses()) have.Add(ip.ToString());
            }
            return have.SetEquals(wanted);
        }
        catch { return false; }
    }

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
