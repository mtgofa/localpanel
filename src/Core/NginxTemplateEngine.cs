using System.IO;
using System.Text.RegularExpressions;

namespace LocalPanel.Core;

/// <summary>
/// Simple template engine for nginx configuration files. Templates live in
/// etc\nginx\templates\ and are editable by the user — their changes survive
/// config regeneration (unlike the generated files in etc\nginx\ which are
/// rewritten every time).
///
/// Syntax:
///   {{VAR}}            — replaced with the variable's value
///   {{IF_VAR}}…{{ENDIF_VAR}}     — included when VAR is set and non-empty
///   {{IF_NOT_VAR}}…{{ENDIF_VAR}} — included when VAR is empty or unset
/// </summary>
public static class NginxTemplateEngine
{
    /// <summary>etc\nginx\templates — user-editable template source files.</summary>
    public static string TemplatesDir => Path.Combine(Paths.NginxEtc, "templates");

    /// <summary>
    /// Load a template by name (e.g. "nginx.conf") from the templates directory.
    /// The file must exist — call <see cref="EnsureDefaults"/> first to create the
    /// built-in defaults on first run.
    /// </summary>
    public static string Load(string templateName)
    {
        return File.ReadAllText(Path.Combine(Paths.Ensure(TemplatesDir), templateName));
    }

    /// <summary>
    /// Write built-in default templates for any files that don't exist yet.
    /// Called once on first launch; after that the user's edits are preserved.
    /// </summary>
    public static void EnsureDefaults()
    {
        var dir = Paths.Ensure(TemplatesDir);

        var nginx = Path.Combine(dir, "nginx.conf");
        if (!File.Exists(nginx)) File.WriteAllText(nginx, DefaultNginxConf);

        var site = Path.Combine(dir, "site.conf");
        if (!File.Exists(site)) File.WriteAllText(site, DefaultSiteConf);

        var localhost = Path.Combine(dir, "_localhost.conf");
        if (!File.Exists(localhost)) File.WriteAllText(localhost, DefaultLocalhostConf);
    }

    /// <summary>
    /// Process a template: resolve conditional blocks, then replace every
    /// {{VAR}} placeholder with its value (empty string when the key is missing).
    /// </summary>
    public static string Process(string template, Dictionary<string, string> vars)
    {
        var result = ResolveConditionals(template, vars);
        foreach (var (key, value) in vars)
            result = result.Replace("{{" + key + "}}", value);
        return result;
    }

    // ---- conditional blocks ----

    private static string ResolveConditionals(string text, Dictionary<string, string> vars)
    {
        // {{IF_VAR}}…{{ENDIF_VAR}} — keep the body when VAR is set and non-empty.
        text = Regex.Replace(text, @"\{\{IF_(\w+)\}\}(.*?)\{\{ENDIF_\1\}\}", m =>
        {
            var varName = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            return vars.TryGetValue(varName, out var v) && v.Length > 0 ? body : "";
        }, RegexOptions.Singleline);

        // {{IF_NOT_VAR}}…{{ENDIF_VAR}} — keep the body when VAR is empty or unset.
        text = Regex.Replace(text, @"\{\{IF_NOT_(\w+)\}\}(.*?)\{\{ENDIF_\1\}\}", m =>
        {
            var varName = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            return !vars.TryGetValue(varName, out var v) || v.Length == 0 ? body : "";
        }, RegexOptions.Singleline);

        return text;
    }

    // ==================== Default template content ====================
    // Written once to etc\nginx\templates\ on first run, then left alone so the
    // user can customise them. Deleting a template file causes it to be recreated
    // with these defaults on the next launch.

    private const string DefaultNginxConf = """
# nginx.conf — main nginx configuration template.
# Edit this file to customise the global nginx settings.
# LocalPanel reads this template on every config generation and fills in variable values.
# PHP upstream pools are auto-generated and placed inside the http block below.
# Delete this file to restore the defaults.

worker_processes 2;
error_log "{{ERROR_LOG}}";
pid       "{{PID_FILE}}";

events {
    worker_connections 1024;
}

http {
{{UPSTREAMS}}
    include       mime.types;
    default_type  application/octet-stream;
    server_names_hash_bucket_size 128;
    server_names_hash_max_size    2048;
    access_log    "{{ACCESS_LOG}}";
    sendfile      on;
    tcp_nopush    on;
    tcp_nodelay   on;
    keepalive_timeout 65;
    client_max_body_size 512m;

    fastcgi_buffer_size 32k;
    fastcgi_buffers 8 16k;
    fastcgi_busy_buffers_size 64k;
    proxy_buffer_size 32k;
    proxy_buffers 8 16k;
    proxy_busy_buffers_size 64k;

    client_body_temp_path "{{CLIENT_BODY_TEMP}}";
    proxy_temp_path       "{{PROXY_TEMP}}";
    fastcgi_temp_path     "{{FASTCGI_TEMP}}";
    uwsgi_temp_path       "{{UWSGI_TEMP}}";
    scgi_temp_path        "{{SCGI_TEMP}}";

    map $http_upgrade $connection_upgrade {
        default upgrade;
        ''      close;
    }

    include "{{SITES_INCLUDE}}/*.conf";
}
""";

    private const string DefaultSiteConf = """
# site.conf — template for every site's server block.
# Variables are replaced on every config generation:
#   LISTEN, SERVER_NAME, ROOT, LOCATIONS
# Delete this file to restore the defaults.

server {
{{LISTEN}}
    server_name {{SERVER_NAME}};
{{ROOT}}

{{LOCATIONS}}
}
""";

    private const string DefaultLocalhostConf = """
# _localhost.conf — template for the localhost welcome page + phpMyAdmin.
# Variables are replaced on every config generation:
#   LISTEN, SERVER_NAME, ROOT, PHPMYADMIN
# Delete this file to restore the defaults.

server {
{{LISTEN}}
    server_name {{SERVER_NAME}};
    root "{{ROOT}}";
    index index.html;
{{PHPMYADMIN}}
}
""";
}
