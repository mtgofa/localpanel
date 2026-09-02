using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LocalPanel.Core;

/// <summary>
/// Generates the full nginx configuration (main conf + one file per site)
/// inside etc/nginx. Sites are either PHP (FastCGI to the version's worker
/// pool), static, or a reverse proxy to a TCP port / unix socket.
/// </summary>
public static class NginxConfigGenerator
{
    // WriteAll wipes and rewrites the whole sites folder; the launch-time apply
    // (background), the auto-start Nginx.StartAsync and UI-triggered applies can
    // overlap, so serialize them or one writer deletes what another just wrote.
    private static readonly object WriteGate = new();

    /// <summary>
    /// Windows/Linux resolves "localhost" and usually answers ::1 before 127.0.0.1.
    /// Listening on [::] as well makes that first attempt succeed.
    /// Probed once: on a machine with IPv6 disabled this listen directive would stop
    /// nginx from starting at all.
    /// </summary>
    private static readonly bool Ipv6 = ProbeIpv6();

    private static bool ProbeIpv6()
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            // Ephemeral port on ::1: proves the stack works without ever clashing
            // with the listener nginx itself is holding.
            using var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when HTTPS is actually served for the site: SSL enabled AND the
    /// mkcert files exist. Single source of truth for RenderSite, the welcome page and
    /// the UI "Open" button so no https:// link points at a listener that isn't there.</summary>
    public static bool HasCert(Site s) =>
        s.Ssl &&
        File.Exists(Path.Combine(Paths.SslDir, s.Domain + ".pem")) &&
        File.Exists(Path.Combine(Paths.SslDir, s.Domain + "-key.pem"));

    public static void WriteAll(AppConfig config)
    {
        lock (WriteGate)
        {
            NginxTemplateEngine.EnsureDefaults();
            WriteMainConf(config);
            WriteFastCgiParams();

            // Render every site first so a single bad site (or IO error) cannot
            // leave the folder half-wiped; a failed render becomes a comment file
            // instead of taking every other site down with it.
            var rendered = new List<(string Target, string Body)>();
            foreach (var site in config.Sites.ToList()) // snapshot: the launch-time import may mutate the list
            {
                var file = Path.Combine(Paths.NginxSites, site.Domain + ".conf");
                string body;
                try { body = RenderSite(config, site); }
                catch (Exception ex) { body = $"# {site.Domain} skipped: {ex.Message}\n"; }
                rendered.Add((file, body));
            }

            // Regenerate the sites folder from scratch so deleted sites disappear.
            foreach (var f in Directory.GetFiles(Paths.NginxSites, "*.conf"))
                File.Delete(f);
            foreach (var (file, body) in rendered)
                File.WriteAllText(file, body);
            WriteLocalhostConf(config);
        }
    }

    /// <summary>
    /// http(s)://localhost — the panel's welcome page, plus phpMyAdmin served
    /// from its own folder at /phpmyadmin (no dedicated vhost).
    /// </summary>
    private static void WriteLocalhostConf(AppConfig config)
    {
        var htmlDir = Paths.Ensure(Path.Combine(Paths.NginxEtc, "html"));
        File.WriteAllText(Path.Combine(htmlDir, "index.html"), RenderWelcomePage(config));

        var cert = Path.Combine(Paths.SslDir, "localhost.pem");
        var key = Path.Combine(Paths.SslDir, "localhost-key.pem");
        var hasCert = File.Exists(cert) && File.Exists(key);

        // Build listen directives
        var listen = new StringBuilder();
        listen.AppendLine($"    listen {config.Nginx.HttpPort} default_server;");
        if (Ipv6) listen.AppendLine($"    listen [::]:{config.Nginx.HttpPort} default_server;");
        if (hasCert)
        {
            listen.AppendLine($"    listen {config.Nginx.HttpsPort} ssl default_server;");
            if (Ipv6) listen.AppendLine($"    listen [::]:{config.Nginx.HttpsPort} ssl default_server;");
            listen.AppendLine($"    ssl_certificate     \"{N(cert)}\";");
            listen.AppendLine($"    ssl_certificate_key \"{N(key)}\";");
            listen.AppendLine("    http2 on;");
        }

        // Same defensive resolve as RenderSite: a stale DefaultPhpVersion must not
        // emit a fastcgi_pass to an upstream that no longer exists.
        var phpVer = PhpInstalled(config, config.DefaultPhpVersion)
            ? config.DefaultPhpVersion : config.Php.FirstOrDefault()?.Version;
        var phpmyadmin = "";
        if (phpVer is not null && Directory.Exists(Paths.PhpMyAdminDir))
        {
            phpmyadmin = $$"""

                location /phpmyadmin {
                    root "{{N(Paths.Bin)}}";
                    index index.php;
                }
                location ~ ^/phpmyadmin/.+\.php$ {
                    root "{{N(Paths.Bin)}}";
                    fastcgi_pass php_{{Slug(phpVer)}};
                    fastcgi_index index.php;
                    fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
                    include fastcgi_params;
                    fastcgi_read_timeout 300;
                }
            """;
        }

        var template = NginxTemplateEngine.Load("_localhost.conf");
        var result = NginxTemplateEngine.Process(template, new Dictionary<string, string>
        {
            ["LISTEN"] = listen.ToString(),
            ["SERVER_NAME"] = "localhost 127.0.0.1 [::1]",
            ["ROOT"] = N(htmlDir),
            ["PHPMYADMIN"] = phpmyadmin,
        });
        File.WriteAllText(Path.Combine(Paths.NginxSites, "_localhost.conf"), result);
    }

    private static string RenderWelcomePage(AppConfig config)
    {
        var sb = new StringBuilder();
        foreach (var site in config.Sites.OrderBy(s => s.Domain))
        {
            var scheme = HasCert(site) ? "https" : "http";
            sb.Append($"<a class=\"card\" href=\"{scheme}://{site.Domain}\">" +
                      $"<span class=\"dot\"></span>{site.Domain}</a>");
        }
        var tools = new StringBuilder();
        if (Directory.Exists(Paths.PhpMyAdminDir))
            tools.Append("<a class=\"tool\" href=\"/phpmyadmin/\">phpMyAdmin</a>");
        if (File.Exists(Paths.MailpitExe))
            tools.Append($"<a class=\"tool\" href=\"http://127.0.0.1:{config.Mailpit.UiPort}\">Mail inbox</a>");

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>LocalPanel</title>
            <style>
              * { margin:0; padding:0; box-sizing:border-box; }
              body { min-height:100vh; background:#1C2333; color:#E9ECF4;
                     font-family:'Segoe UI',system-ui,sans-serif; display:flex;
                     flex-direction:column; align-items:center; padding:64px 24px; }
              .logo { font-size:44px; font-weight:800; letter-spacing:-1px; }
              .logo b { color:#4F8CFF; }
              .tag { color:#8D96AC; margin:10px 0 34px; font-size:15px; }
              .tools { display:flex; gap:12px; margin-bottom:40px; flex-wrap:wrap; justify-content:center; }
              .tool { background:#4F8CFF; color:#fff; text-decoration:none; padding:11px 22px;
                      border-radius:8px; font-weight:600; font-size:15px; }
              .tool:hover { background:#6AA0FF; }
              h2 { font-size:14px; color:#8D96AC; text-transform:uppercase; letter-spacing:1px;
                   margin-bottom:14px; }
              .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(240px,1fr));
                      gap:10px; width:100%; max-width:860px; }
              .card { background:#232B3D; border:1px solid #2E3850; color:#E9ECF4;
                      text-decoration:none; padding:13px 16px; border-radius:9px; font-size:15px;
                      display:flex; align-items:center; gap:10px; }
              .card:hover { border-color:#4F8CFF; }
              .dot { width:8px; height:8px; border-radius:50%; background:#3FD68F; flex:none; }
              .foot { margin-top:auto; padding-top:40px; color:#5A647F; font-size:13px; }
            </style>
            </head>
            <body>
              <div class="logo">Local<b>Panel</b></div>
              <div class="tag">Your local dev environment is up and running</div>
              <div class="tools">{{tools}}</div>
              <h2>Sites</h2>
              <div class="grid">{{sb}}</div>
              <div class="foot">Served by LocalPanel &middot; nginx</div>
            </body>
            </html>
            """;
    }

    private static void WriteMainConf(AppConfig config)
    {
        // Build dynamic upstream blocks (PHP pools + proxy upstreams)
        var upstreams = new StringBuilder();
        foreach (var php in config.Php)
        {
            upstreams.AppendLine($"    upstream php_{Slug(php.Version)} {{");
            for (var i = 0; i < php.Workers; i++)
                upstreams.AppendLine($"        server 127.0.0.1:{php.FastCgiPort + i};");
            upstreams.AppendLine("    }");
        }
        WriteProxyUpstreams(upstreams, config);

        // Copy mime.types from the nginx distribution next to our conf.
        var distMime = Path.Combine(Paths.NginxDir, "conf", "mime.types");
        var ourMime = Path.Combine(Paths.NginxEtc, "mime.types");
        if (File.Exists(distMime)) File.Copy(distMime, ourMime, overwrite: true);
        else if (!File.Exists(ourMime))
            File.WriteAllText(ourMime, """
                types {
                    text/html html htm;
                    text/css css;
                    text/plain txt;
                    application/javascript js mjs;
                    application/json json;
                    application/xml xml;
                    image/png png;
                    image/jpeg jpg jpeg;
                    image/gif gif;
                    image/svg+xml svg;
                    image/webp webp;
                    image/x-icon ico;
                    font/woff woff;
                    font/woff2 woff2;
                    application/zip zip;
                    application/pdf pdf;
                }
                """);

        // Load the user-editable template (written once on first run, then left alone
        // so customisations survive regeneration).
        var template = NginxTemplateEngine.Load("nginx.conf");
        var result = NginxTemplateEngine.Process(template, new Dictionary<string, string>
        {
            ["ERROR_LOG"] = N(Path.Combine(Paths.Logs, "nginx-error.log")),
            ["PID_FILE"] = N(Path.Combine(Paths.Run, "nginx-master.pid")),
            ["ACCESS_LOG"] = N(Path.Combine(Paths.Logs, "nginx-access.log")),
            ["CLIENT_BODY_TEMP"] = N(Path.Combine(Paths.NginxTemp, "client_body")),
            ["PROXY_TEMP"] = N(Path.Combine(Paths.NginxTemp, "proxy")),
            ["FASTCGI_TEMP"] = N(Path.Combine(Paths.NginxTemp, "fastcgi")),
            ["UWSGI_TEMP"] = N(Path.Combine(Paths.NginxTemp, "uwsgi")),
            ["SCGI_TEMP"] = N(Path.Combine(Paths.NginxTemp, "scgi")),
            // Indent each upstream line by 4 spaces so it sits inside the http block.
            ["UPSTREAMS"] = upstreams.Length > 0
                ? string.Join("\n", upstreams.ToString().Split('\n').Select(l => "    " + l))
                : "",
            ["SITES_INCLUDE"] = N(Paths.NginxSites),
        });
        File.WriteAllText(Path.Combine(Paths.NginxEtc, "nginx.conf"), result);
    }

    private static string RenderSite(AppConfig config, Site site)
    {
        var certFile = Path.Combine(Paths.SslDir, site.Domain + ".pem");
        var keyFile = Path.Combine(Paths.SslDir, site.Domain + "-key.pem");
        var hasCert = HasCert(site);
        var serverNames = string.Join(' ', new[] { site.Domain }.Concat(site.Aliases));

        // Build listen directives
        var listen = new StringBuilder();
        if (!hasCert)
        {
            listen.AppendLine($"    listen {config.Nginx.HttpPort};");
            if (Ipv6) listen.AppendLine($"    listen [::]:{config.Nginx.HttpPort};");
        }
        else
        {
            listen.AppendLine($"    listen {config.Nginx.HttpsPort} ssl;");
            if (Ipv6) listen.AppendLine($"    listen [::]:{config.Nginx.HttpsPort} ssl;");
            listen.AppendLine($"    ssl_certificate     \"{N(certFile)}\";");
            listen.AppendLine($"    ssl_certificate_key \"{N(keyFile)}\";");
            listen.AppendLine("    http2 on;");
        }

        // A file-serving "/" route emits its own root; adding one here as well is a
        // duplicate directive, which nginx rejects outright. Only fall back to the
        // site folder when no such route exists (proxy-only sites, or none at all),
        // so requests nothing matches still resolve somewhere sane.
        var rootRoute = site.Routes.FirstOrDefault(r => RoutePath(r) == "/");
        var root = (rootRoute is null || rootRoute.Kind == RouteKind.Proxy)
            ? $"    root \"{N(site.ResolvedRoot)}\";" : "";

        // Resolve defensively: a site can stay bound to a PHP version whose folder was
        // deleted (SyncPhpPools drops the pool but not Site.PhpVersion), and
        // `fastcgi_pass php_x` with no matching upstream invalidates the WHOLE nginx
        // config. Fall back to an installed version; if none is installed the PHP
        // routes are parked so every other site (and route) keeps working.
        var phpVersion = PhpInstalled(config, site.PhpVersion) ? site.PhpVersion
            : PhpInstalled(config, config.DefaultPhpVersion) ? config.DefaultPhpVersion
            : config.Php.FirstOrDefault()?.Version;

        // Build location blocks (sub-paths before "/" purely for readability — nginx
        // picks the longest matching prefix whatever order the locations appear in).
        var locations = new StringBuilder();
        var routes = site.Routes.Count > 0 ? site.Routes : new List<SiteRoute> { new() };
        foreach (var route in routes
                     .OrderBy(r => RoutePath(r) == "/")
                     .ThenByDescending(r => RoutePath(r).Length))
            RenderRoute(locations, site, route, phpVersion);

        // Load the user-editable site template and fill in the variables.
        var template = NginxTemplateEngine.Load("site.conf");
        var mainBlock = NginxTemplateEngine.Process(template, new Dictionary<string, string>
        {
            ["LISTEN"] = listen.ToString(),
            ["SERVER_NAME"] = serverNames,
            ["ROOT"] = root,
            ["LOCATIONS"] = locations.ToString(),
        });

        // When the site has a certificate, prepend an HTTP→HTTPS redirect server block
        // so every asset benefits from HTTP/2 (browsers only negotiate h2 over TLS).
        if (hasCert)
        {
            var redirect = new StringBuilder();
            redirect.AppendLine("server {");
            redirect.AppendLine($"    listen {config.Nginx.HttpPort};");
            if (Ipv6) redirect.AppendLine($"    listen [::]:{config.Nginx.HttpPort};");
            redirect.AppendLine($"    server_name {serverNames};");
            redirect.AppendLine("    return 301 https://$host$request_uri;");
            redirect.AppendLine("}");
            redirect.AppendLine();
            redirect.Append(mainBlock);
            return redirect.ToString();
        }

        return mainBlock;
    }

    /// <summary>Route path as nginx sees it: leading slash, no trailing slash, "/" for the site itself.</summary>
    private static string RoutePath(SiteRoute route)
    {
        var path = (route.Path ?? "").Trim();
        if (path.Length > 0 && path[0] != '/') path = "/" + path;
        if (path.Length > 1) path = path.TrimEnd('/');
        return path.Length > 1 ? path : "/";
    }

    /// <summary>A path turned into a name usable in an nginx named location (@php_api).</summary>
    private static string RouteSlug(string path)
    {
        var slug = new string(path.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return slug.Length == 0 ? "root" : slug;
    }

    private static void RenderRoute(StringBuilder sb, Site site, SiteRoute route, string? phpVersion)
    {
        var path = RoutePath(route);
        var isRoot = path == "/";
        var folder = N(site.ResolvedFolder(route));

        switch (route.Kind)
        {
            case RouteKind.Proxy:
                RenderProxyRoute(sb, site, route, path, isRoot);
                break;

            case RouteKind.Static:
                RenderStaticRoute(sb, path, isRoot, folder, route.SpaFallback);
                break;

            case RouteKind.Php:
            default:
                if (phpVersion is null)
                {
                    sb.AppendLine($"    # {path}: no PHP version is installed - install one from the PHP page.");
                    sb.AppendLine($"    location {(isRoot ? "/" : "^~ " + path)} {{ return 503; }}");
                    break;
                }
                RenderPhpRoute(sb, path, isRoot, folder, phpVersion);
                break;
        }
    }

    private static void RenderStaticRoute(StringBuilder sb, string path, bool isRoot, string folder, bool spa)
    {
        // An SPA router owns every unknown URI, so nothing under it may 404. The fallback
        // is a URI, which under an alias means the route path plus index.html.
        var fallback = spa ? (isRoot ? "/index.html?$args" : $"{path}/index.html?$args") : "=404";
        if (isRoot)
        {
            sb.AppendLine($"    root \"{folder}\";");
            sb.AppendLine("    index index.html index.htm;");
            sb.AppendLine($"    location / {{ try_files $uri $uri/ {fallback}; }}");
            return;
        }
        // ^~ so a PHP route's regex location cannot steal files out of this folder.
        sb.AppendLine($$"""
            location ^~ {{path}} {
                alias "{{folder}}";
                index index.html index.htm;
                try_files $uri $uri/ {{fallback}};
            }
        """);
    }

    private static void RenderPhpRoute(StringBuilder sb, string path, bool isRoot, string folder, string phpVersion)
    {
        if (isRoot)
        {
            sb.AppendLine($"    root \"{folder}\";");
            sb.AppendLine("    index index.php index.html;");
            sb.AppendLine("    location / { try_files $uri $uri/ /index.php?$query_string; }");
            sb.AppendLine($$"""
                location ~ \.php$ {
                    try_files $uri =404;
                    fastcgi_pass php_{{Slug(phpVersion)}};
                    fastcgi_index index.php;
                    fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
                    include fastcgi_params;
                    fastcgi_read_timeout 300;
                }
            """);
            return;
        }

        // A framework mounted under a sub-path: the folder is attached with alias, the
        // front controller is reached through a named location, and SCRIPT_FILENAME has
        // to come from $request_filename because $document_root ignores the alias.
        var at = "@php_" + RouteSlug(path);
        sb.AppendLine($$"""
            location ^~ {{path}} {
                alias "{{folder}}";
                index index.php index.html;
                try_files $uri $uri/ {{at}};

                location ~ \.php$ {
                    fastcgi_pass php_{{Slug(phpVersion)}};
                    fastcgi_index index.php;
                    fastcgi_param SCRIPT_FILENAME $request_filename;
                    include fastcgi_params;
                    fastcgi_read_timeout 300;
                }
            }
            location {{at}} {
                rewrite ^{{path}}/?(.*)$ {{path}}/index.php?$args last;
            }
        """);
    }

    /// <summary>
    /// Stable upstream names for a site's proxy routes, so RenderSite and WriteMainConf
    /// agree on them. Two different paths can slugify to the same word (/api-v1 and
    /// /api_v1), hence the de-duplication.
    /// </summary>
    private static Dictionary<SiteRoute, string> ProxyUpstreams(Site site)
    {
        var names = new Dictionary<SiteRoute, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in site.Routes.Where(r => r.Kind == RouteKind.Proxy &&
                                                     string.IsNullOrWhiteSpace(r.SocketPath)))
        {
            var stem = $"lp_{Slug(site.Domain)}_{RouteSlug(RoutePath(route))}";
            var name = stem;
            for (var i = 2; !used.Add(name); i++) name = $"{stem}_{i}";
            names[route] = name;
        }
        return names;
    }

    /// <summary>
    /// A dev server told to listen on "localhost" may bind ::1 or 127.0.0.1.
    /// Rather than making the user work out which, every proxy route gets both loopbacks, with the
    /// one that is actually listening right now put first.
    /// </summary>
    private static void WriteProxyUpstreams(StringBuilder sb, AppConfig config)
    {
        foreach (var site in config.Sites)
        {
            foreach (var (route, name) in ProxyUpstreams(site))
            {
                var v6First = Ipv6 &&
                              !Listening(IPAddress.Loopback, route.Port) &&
                              Listening(IPAddress.IPv6Loopback, route.Port);
                var v4 = $"server 127.0.0.1:{route.Port}";
                var v6 = $"server [::1]:{route.Port}";
                sb.AppendLine($"    upstream {name} {{");
                sb.AppendLine($"        {(v6First ? v6 : v4)} max_fails=1 fail_timeout=10s;");
                if (Ipv6) sb.AppendLine($"        {(v6First ? v4 : v6)} backup;");
                sb.AppendLine("    }");
            }
        }
    }

    private static bool Listening(IPAddress address, int port)
    {
        try
        {
            using var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                Blocking = false
            };
            try { s.Connect(new IPEndPoint(address, port)); return true; } // connected outright
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock
                                                                or SocketError.InProgress) { }
            return s.Poll(250_000, SelectMode.SelectWrite);
        }
        catch { return false; }
    }

    private static void RenderProxyRoute(StringBuilder sb, Site site, SiteRoute route, string path, bool isRoot)
    {
        var target = string.IsNullOrWhiteSpace(route.SocketPath)
            ? $"http://{ProxyUpstreams(site).GetValueOrDefault(route, $"127.0.0.1:{route.Port}")}"
            : $"http://unix:{N(route.SocketPath!)}";
        var head = isRoot ? "location /" : $"location ^~ {path}";

        sb.AppendLine($$"""
            {{head}} {
                proxy_pass {{target}};
                proxy_http_version 1.1;
                proxy_set_header Host $host;
                proxy_set_header X-Real-IP $remote_addr;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                proxy_set_header X-Forwarded-Proto $scheme;
                proxy_set_header Upgrade $http_upgrade;
                proxy_set_header Connection $connection_upgrade;
                proxy_connect_timeout 1s;
                proxy_read_timeout 3600;
                proxy_send_timeout 3600;
                proxy_buffering off;
            }
        """);
    }

    private static void WriteFastCgiParams()
    {
        // Standard fastcgi_params from the nginx distribution.
        var dist = Path.Combine(Paths.NginxDir, "conf", "fastcgi_params");
        var ours = Path.Combine(Paths.NginxEtc, "fastcgi_params");
        if (File.Exists(dist)) File.Copy(dist, ours, overwrite: true);
        else if (!File.Exists(ours))
            File.WriteAllText(ours, """
                fastcgi_param QUERY_STRING    $query_string;
                fastcgi_param REQUEST_METHOD  $request_method;
                fastcgi_param CONTENT_TYPE    $content_type;
                fastcgi_param CONTENT_LENGTH  $content_length;
                fastcgi_param REQUEST_URI     $request_uri;
                fastcgi_param DOCUMENT_URI    $document_uri;
                fastcgi_param DOCUMENT_ROOT   $document_root;
                fastcgi_param SERVER_PROTOCOL $server_protocol;
                fastcgi_param GATEWAY_INTERFACE CGI/1.1;
                fastcgi_param SERVER_SOFTWARE nginx/$nginx_version;
                fastcgi_param REMOTE_ADDR     $remote_addr;
                fastcgi_param REMOTE_PORT     $remote_port;
                fastcgi_param SERVER_ADDR     $server_addr;
                fastcgi_param SERVER_PORT     $server_port;
                fastcgi_param SERVER_NAME     $server_name;
                """);
    }

    /// <summary>True when an upstream block will exist for this PHP version.</summary>
    private static bool PhpInstalled(AppConfig config, string? version) =>
        version is not null && config.Php.Any(p => p.Version == version);

    /// <summary>Nginx wants forward slashes on both Windows and Linux.</summary>
    private static string N(string path) => path.Replace('\\', '/');

    private static string Slug(string version) => version.Replace('.', '_').Replace('-', '_');
}
