using System.Net.Sockets;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.Signing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Sign;

/// <summary>
/// Sign a Windows artifact (MSIX/EXE/MSI/…) with a pks-held code-signing certificate. Unattended by
/// design — no second-factor prompt — so it runs in CI. Two key sources, in order:
///   1. the local pks cert store (host running the job, or anywhere `pks cert init` was run);
///   2. the runner credential socket (inside a job container) — fetches a short-lived PFX from the
///      host that ran `pks github runner start`, so the encrypted key + KEK never enter the container.
/// </summary>
public class SignCommand : Command<SignSettings>
{
    private readonly ICertStore _store;
    private readonly IEnumerable<ISignProvider> _providers;
    private readonly IAnsiConsole _console;

    public SignCommand(ICertStore store, IEnumerable<ISignProvider> providers, IAnsiConsole console)
    {
        _store = store;
        _providers = providers;
        _console = console;
    }

    public override int Execute(CommandContext context, SignSettings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(SignSettings settings)
    {
        if (!File.Exists(settings.Input))
        {
            _console.MarkupLine($"[red]Input not found:[/] {settings.Input.EscapeMarkup()}");
            return 1;
        }

        var output = settings.Output ?? DefaultOutput(settings.Input);
        var request = new SignRequest(settings.Input, output, settings.Timestamp);

        // 1. Local store.
        var record = await ResolveLocalCertAsync(settings.Cert);
        if (record != null)
        {
            var provider = _providers.FirstOrDefault(p => p.CanHandle(record.Provider));
            if (provider == null)
            {
                _console.MarkupLine($"[red]No signing provider for {record.Provider}[/] (not yet supported).");
                return 1;
            }

            var result = await provider.SignAsync(record, _store, request);
            return Report(result);
        }

        // 2. Container path — fetch a materialized PFX from the runner credential socket.
        var token = Environment.GetEnvironmentVariable("PKS_TOKEN");
        var sockUrl = Environment.GetEnvironmentVariable("PKS_TOKEN_URL");
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(sockUrl) && File.Exists(sockUrl))
        {
            var ossl = _providers.OfType<OsslSignProvider>().FirstOrDefault();
            if (ossl == null)
            {
                _console.MarkupLine("[red]osslsigncode provider unavailable.[/]");
                return 1;
            }

            string? tempPfx = null;
            try
            {
                var fetched = await FetchPfxFromSocketAsync(sockUrl, token, settings.Cert);
                if (fetched == null)
                {
                    _console.MarkupLine("[red]The runner host has no pks-held certificate to serve.[/] Run [cyan]pks cert init[/] on the runner host.");
                    return 1;
                }

                tempPfx = Path.Combine(Path.GetTempPath(), $"pks-cert-fetch-{Guid.NewGuid():n}.pfx");
                using (var fs = new FileStream(tempPfx, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    SecurityFiles.Restrict(tempPfx);
                    await fs.WriteAsync(Convert.FromBase64String(fetched.Value.PfxBase64));
                }
                SecurityFiles.Restrict(tempPfx);

                var result = await ossl.SignWithPfxAsync(tempPfx, fetched.Value.Password, request);
                return Report(result);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Signing via runner socket failed:[/] {ex.Message.EscapeMarkup()}");
                return 1;
            }
            finally
            {
                try { if (tempPfx != null && File.Exists(tempPfx)) File.Delete(tempPfx); } catch { /* best effort */ }
            }
        }

        _console.MarkupLine("[red]No certificate available.[/] Run [cyan]pks cert init[/] (host) or run inside a pks runner container.");
        return 1;
    }

    private int Report(SignResult result)
    {
        if (result.Success) { _console.MarkupLine($"[green]✓ {result.Message.EscapeMarkup()}[/]"); return 0; }
        _console.MarkupLine($"[red]{result.Message.EscapeMarkup()}[/]");
        return 1;
    }

    private async Task<CertRecord?> ResolveLocalCertAsync(string? idOrLabel)
    {
        if (!string.IsNullOrWhiteSpace(idOrLabel)) return await _store.FindAsync(idOrLabel);
        var all = await _store.ListAsync();
        if (all.Count == 1) return all[0];
        if (all.Count > 1)
            _console.MarkupLine("[yellow]Multiple certs found — specify one with -c <id|label>.[/]");
        return null;
    }

    private static string DefaultOutput(string input)
    {
        var dir = Path.GetDirectoryName(input) ?? "";
        var name = Path.GetFileNameWithoutExtension(input);
        var ext = Path.GetExtension(input);
        return Path.Combine(dir, $"{name}-signed{ext}");
    }

    private static async Task<(string PfxBase64, string Password)?> FetchPfxFromSocketAsync(string socketPath, string token, string? certId)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        using var http = new HttpClient(handler);
        var url = "http://localhost/cert/pfx" + (string.IsNullOrWhiteSpace(certId) ? "" : $"?id={Uri.EscapeDataString(certId)}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        var pfx = root.GetProperty("pfxBase64").GetString();
        var pwd = root.GetProperty("password").GetString();
        if (pfx == null || pwd == null) return null;
        return (pfx, pwd);
    }
}
