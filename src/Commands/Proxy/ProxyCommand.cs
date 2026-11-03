using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace PKS.Commands.Proxy;

/// <summary>
/// A local HTTPS reverse proxy: answers on 443 for one or more hostnames, with certificates from a
/// local CA the machine trusts once, and forwards to whatever runs behind them (YARP, so websockets
/// and HMR pass through). For a demo on a real-looking address that the hosts file points at
/// 127.0.0.1, without Caddy, mkcert or a browser warning.
/// </summary>
[Description("Serve local dev servers over HTTPS on real-looking hostnames (local CA, trusted once)")]
public class ProxyCommand : AsyncCommand<ProxyCommand.Settings>
{
    private const string CaLabel = "pks-proxy-ca";
    private static readonly TimeSpan CaValidity = TimeSpan.FromDays(3650);
    private static readonly TimeSpan ServerCertValidity = TimeSpan.FromDays(30);

    private readonly IAnsiConsole _console;

    public ProxyCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[host]")]
        [Description("Hostname to answer on, e.g. platform.contextand.com")]
        public string? Host { get; set; }

        [CommandOption("--to <URL>")]
        [Description("Where requests for <host> go, e.g. http://localhost:5173")]
        public string? To { get; set; }

        [CommandOption("--route <HOST=URL>")]
        [Description("Further hostname=upstream pairs (repeatable)")]
        public string[]? Routes { get; set; }

        [CommandOption("--port <PORT>")]
        [Description("Local HTTPS port (default 443)")]
        [DefaultValue(443)]
        public int Port { get; set; } = 443;

        [CommandOption("--no-trust")]
        [Description("Do not offer to install the local CA in the trust store")]
        public bool NoTrust { get; set; }

        [CommandOption("--remove-trust")]
        [Description("Remove the local CA from the trust store and exit")]
        public bool RemoveTrust { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var store = new CertStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "proxy-ca"));

        if (settings.RemoveTrust)
            return await RemoveTrustAsync(store);

        List<ProxyRoute> routes;
        try
        {
            routes = ParseRoutes(settings.Host, settings.To, settings.Routes);
        }
        catch (ArgumentException ex)
        {
            _console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            _console.MarkupLine("[dim]Example: pks proxy platform.contextand.com --to http://localhost:5173[/]");
            return 1;
        }

        using var root = await LoadOrCreateRootAsync(store);
        var caFile = ExportRootCer(root);

        if (!settings.NoTrust)
            EnsureTrusted(root, caFile);

        var certificates = routes.ToDictionary(
            r => r.Host,
            r => LocalCertificateAuthority.IssueServerCertificate(root, [r.Host], ServerCertValidity),
            StringComparer.OrdinalIgnoreCase);
        var fallback = certificates[routes[0].Host];

        foreach (var route in routes)
            await WarnUnlessLoopbackAsync(route.Host);

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(settings.Port, listen =>
            listen.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificateSelector = (_, name) =>
                    name is not null && certificates.TryGetValue(name, out var cert) ? cert : fallback,
            })));
        builder.Services.AddHttpForwarder();

        var app = builder.Build();
        foreach (var route in routes)
        {
            // The original Host goes upstream, so dev servers with a host allow-list (Vite) accept it
            // and apps that build absolute URLs build them on the public name.
            app.MapForwarder("/{**catch-all}", route.Upstream.ToString().TrimEnd('/'), ForwarderRequestConfig.Empty,
                    transforms => transforms.AddOriginalHost(true))
                .RequireHost(route.Host);
        }
        app.MapFallback((HttpContext ctx) => Results.Text(
            $"pks proxy has no route for '{ctx.Request.Host.Host}'.", statusCode: StatusCodes.Status404NotFound));

        try
        {
            await app.StartAsync();
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            _console.MarkupLine($"[red]Could not listen on port {settings.Port}:[/] {Markup.Escape(ex.Message)}");
            _console.MarkupLine("[dim]Something else may hold the port, or it needs elevated rights on this OS. Try --port 8443.[/]");
            return 1;
        }

        var portSuffix = settings.Port == 443 ? "" : $":{settings.Port}";
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Open").AddColumn("Forwards to");
        foreach (var route in routes)
            table.AddRow($"[green]https://{route.Host}{portSuffix}[/]", Markup.Escape(route.Upstream.ToString()));
        _console.Write(table);
        _console.MarkupLine("[dim]Ctrl+C to stop.[/]");

        await app.WaitForShutdownAsync();
        foreach (var cert in certificates.Values)
            cert.Dispose();
        return 0;
    }

    public sealed record ProxyRoute(string Host, Uri Upstream);

    /// <summary>The positional host with <c>--to</c>, followed by every <c>--route host=url</c>.</summary>
    public static List<ProxyRoute> ParseRoutes(string? host, string? to, IEnumerable<string>? extra)
    {
        var routes = new List<ProxyRoute>();
        if (!string.IsNullOrWhiteSpace(host) || !string.IsNullOrWhiteSpace(to))
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(to))
                throw new ArgumentException("A host needs --to, and --to needs a host.");
            routes.Add(Route(host, to));
        }

        foreach (var pair in extra ?? [])
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || eq == pair.Length - 1)
                throw new ArgumentException($"--route '{pair}' is not host=url.");
            routes.Add(Route(pair[..eq], pair[(eq + 1)..]));
        }

        if (routes.Count == 0)
            throw new ArgumentException("Nothing to proxy. Give a host and --to, or --route host=url.");

        var duplicate = routes.GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"'{duplicate.Key}' is routed twice.");
        return routes;
    }

    private static ProxyRoute Route(string host, string upstream)
    {
        host = host.Trim().ToLowerInvariant();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw new ArgumentException($"'{host}' is not a hostname.");
        if (!Uri.TryCreate(upstream.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException($"'{upstream}' is not an http(s) URL.");
        return new ProxyRoute(host, uri);
    }

    private async Task<X509Certificate2> LoadOrCreateRootAsync(CertStore store)
    {
        var record = await store.FindAsync(CaLabel);
        if (record is null || record.NotAfter < DateTime.UtcNow.AddDays(30))
        {
            if (record is not null)
                await store.RemoveAsync(record.Id);
            using var created = LocalCertificateAuthority.CreateRoot(CaValidity);
            await store.ImportPfxAsync(created.Export(X509ContentType.Pkcs12), null, CaLabel);
            _console.MarkupLine("[dim]Created a local certificate authority for pks proxy.[/]");
            record = await store.FindAsync(CaLabel)
                ?? throw new InvalidOperationException("The local CA was not stored.");
        }

        using var pfx = await store.MaterializePfxAsync(record.Id);
        return X509CertificateLoader.LoadPkcs12FromFile(pfx.Path, pfx.Password, X509KeyStorageFlags.Exportable);
    }

    private static string ExportRootCer(X509Certificate2 root)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "proxy-ca", "pks-proxy-ca.cer");
        File.WriteAllBytes(path, root.Export(X509ContentType.Cert));
        return path;
    }

    private void EnsureTrusted(X509Certificate2 root, string caFile)
    {
        if (OperatingSystem.IsWindows())
        {
            // The current user's root store needs no admin; Windows itself asks the person to confirm.
            using var trust = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            trust.Open(OpenFlags.ReadWrite);
            if (trust.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false).Count > 0)
                return;

            _console.MarkupLine("[yellow]Installing the pks proxy CA. Windows will ask you to confirm: answer Yes.[/]");
            try
            {
                using var publicOnly = X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert));
                trust.Add(publicOnly);
                _console.MarkupLine("[green]CA trusted.[/] [dim]Restart the browser if it was already open.[/]");
            }
            catch (CryptographicException)
            {
                _console.MarkupLine("[yellow]The CA was not installed, so the browser will warn. Run again to be asked again.[/]");
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var keychain = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");
            var check = Process.Start(new ProcessStartInfo("security", ["verify-cert", "-c", caFile]) { RedirectStandardOutput = true, RedirectStandardError = true });
            check?.WaitForExit();
            if (check?.ExitCode == 0)
                return;

            _console.MarkupLine("[yellow]Installing the pks proxy CA. macOS will ask for your password.[/]");
            var add = Process.Start(new ProcessStartInfo("security", ["add-trusted-cert", "-r", "trustRoot", "-k", keychain, caFile]));
            add?.WaitForExit();
            if (add?.ExitCode != 0)
                _console.MarkupLine("[yellow]The CA was not installed, so the browser will warn.[/]");
            return;
        }

        _console.MarkupLine($"[dim]Trust the CA once to avoid browser warnings: {Markup.Escape(caFile)}[/]");
        _console.MarkupLine("[dim]  e.g. sudo cp it to /usr/local/share/ca-certificates/pks-proxy-ca.crt && sudo update-ca-certificates[/]");
    }

    private async Task<int> RemoveTrustAsync(CertStore store)
    {
        var record = await store.FindAsync(CaLabel);
        if (record is null)
        {
            _console.MarkupLine("[dim]There is no pks proxy CA.[/]");
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            using var trust = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            trust.Open(OpenFlags.ReadWrite);
            foreach (var cert in trust.Certificates.Find(X509FindType.FindByThumbprint, record.Thumbprint, false))
                trust.Remove(cert);
        }
        else
        {
            _console.MarkupLine("[dim]Remove 'pks proxy local CA' from the system trust store by hand.[/]");
        }

        await store.RemoveAsync(record.Id);
        _console.MarkupLine("[green]The pks proxy CA is removed.[/] [dim]A new one is made on the next run.[/]");
        return 0;
    }

    private async Task WarnUnlessLoopbackAsync(string host)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host);
        }
        catch (System.Net.Sockets.SocketException)
        {
            addresses = [];
        }

        if (addresses.Length > 0 && addresses.All(IPAddress.IsLoopback))
            return;

        var hostsFile = OperatingSystem.IsWindows() ? @"C:\Windows\System32\drivers\etc\hosts" : "/etc/hosts";
        _console.MarkupLine($"[yellow]{Markup.Escape(host)} does not point at this machine.[/] [dim]Add to {Markup.Escape(hostsFile)} (as admin):[/]");
        _console.MarkupLine($"  127.0.0.1 {Markup.Escape(host)}");
    }
}