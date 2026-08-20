namespace PKS.Commands.T3;

/// <summary>
/// The bash that turns a bare Ubuntu VM into a T3 Code box.
///
/// It is generated rather than shipped as a file because three of its values — the domain, the
/// Entra client id, the Foundry deployment — are only known at provisioning time, and templating
/// them into a checked-in script would mean either a second templating language or a script that
/// reads a config file that also has to get there. The one value that is *not* interpolated is the
/// client secret: it arrives on stdin (see <see cref="SecretDeliveryScript"/>), because everything
/// in <see cref="Build"/> becomes an ssh argv and therefore a line in the remote shell's history.
///
/// Every step is idempotent. Re-running <c>pks t3 init</c> against an existing box is the supported
/// way to change the domain or rotate the secret, so a step that appends rather than replaces (a
/// second Caddy site block, a duplicated systemd unit) would break the second run rather than the
/// first — the failure that is expensive to find.
/// </summary>
public static class T3BootstrapScript
{
    /// <summary>Where t3 listens. Loopback only — Caddy and oauth2-proxy are the only things that
    /// ever reach it, and the box's firewall does not open this port.</summary>
    public const int T3Port = 3773;

    /// <summary>oauth2-proxy's listener, also loopback.</summary>
    public const int OAuthProxyPort = 4180;

    /// <summary>The persistent Foundry passthrough that Codex points at.</summary>
    public const int FoundryProxyPort = 8788;

    /// <summary>The path oauth2-proxy reserves for the OIDC handshake, and therefore the tail of the
    /// redirect URI that has to exist on the Entra app registration.</summary>
    public const string CallbackPath = "/oauth2/callback";

    public static string RedirectUriFor(string domain) => $"https://{domain}{CallbackPath}";

    /// <summary>
    /// Phase 1 — packages, Node, the agent CLIs, and the two proxies. Everything here is public
    /// information and safe to appear in an ssh argv.
    /// </summary>
    public static string Build(T3BootstrapOptions o)
    {
        // Emails go in the Caddy global block for ACME; an empty one makes Caddy prompt, which in a
        // non-interactive ssh session is a hang rather than an error.
        var acmeEmail = string.IsNullOrWhiteSpace(o.AcmeEmail) ? "admin@" + o.Domain : o.AcmeEmail;

        return $$"""
        set -euo pipefail

        echo "==> pks t3: bootstrapping {{o.Domain}}"

        export DEBIAN_FRONTEND=noninteractive
        sudo -n true 2>/dev/null || { echo "pks t3: passwordless sudo is required on this VM" >&2; exit 78; }

        # ---------------------------------------------------------------- packages
        sudo apt-get update -qq
        sudo apt-get install -y -qq curl ca-certificates gnupg debian-keyring debian-archive-keyring apt-transport-https jq

        # ---------------------------------------------------------------- node 22
        # t3 requires ^22.16 || ^23.11 || >=24.10. Ubuntu's own nodejs is far below that on every
        # LTS image, so NodeSource is not optional here.
        if ! node --version 2>/dev/null | grep -qE '^v(2[2-9]|[3-9][0-9])'; then
          curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
          sudo apt-get install -y -qq nodejs
        fi
        node --version

        # ---------------------------------------------------------------- CLIs
        # t3 itself, the agent it will drive, and pks (for the Foundry passthrough below).
        sudo npm install -g --silent t3@latest @openai/codex pks-cli

        # ---------------------------------------------------------------- caddy
        if ! command -v caddy >/dev/null; then
          curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
            | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
          curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
            | sudo tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
          sudo apt-get update -qq
          sudo apt-get install -y -qq caddy
        fi

        # ---------------------------------------------------------------- oauth2-proxy
        OAUTH2_PROXY_VERSION={{o.OAuth2ProxyVersion}}
        if [ ! -x /usr/local/bin/oauth2-proxy ] || ! /usr/local/bin/oauth2-proxy --version 2>&1 | grep -q "$OAUTH2_PROXY_VERSION"; then
          ARCH=$(dpkg --print-architecture)
          TARBALL="oauth2-proxy-v${OAUTH2_PROXY_VERSION}.linux-${ARCH}"
          curl -fsSL -o /tmp/o2p.tar.gz \
            "https://github.com/oauth2-proxy/oauth2-proxy/releases/download/v${OAUTH2_PROXY_VERSION}/${TARBALL}.tar.gz"
          tar -xzf /tmp/o2p.tar.gz -C /tmp
          sudo install -m 0755 "/tmp/${TARBALL}/oauth2-proxy" /usr/local/bin/oauth2-proxy
          rm -rf /tmp/o2p.tar.gz "/tmp/${TARBALL}"
        fi

        sudo install -d -m 0750 -o root -g root /etc/pks-t3

        # ---------------------------------------------------------------- oauth2-proxy config
        # Only the non-secret half. The client secret and cookie secret are written by the stdin
        # phase into /etc/pks-t3/oauth2-proxy.env, which this file does not contain and cannot leak.
        sudo tee /etc/pks-t3/oauth2-proxy.cfg >/dev/null <<'O2PCFG'
        provider = "oidc"
        provider_display_name = "Microsoft Entra ID"
        oidc_issuer_url = "https://login.microsoftonline.com/{{o.TenantId}}/v2.0"
        redirect_url = "https://{{o.Domain}}{{CallbackPath}}"
        http_address = "127.0.0.1:{{OAuthProxyPort}}"
        upstreams = ["http://127.0.0.1:{{T3Port}}/"]
        cookie_secure = true
        cookie_httponly = true
        cookie_expire = "168h"
        # T3's web app holds a websocket open to its own backend. That upgrade carries the session
        # cookie because it is same-origin, so it authenticates like any other request — but it only
        # gets *proxied* if this is on. It defaults to true; stated because turning it off looks
        # harmless and breaks the entire UI in a way that reads as "t3 is down".
        proxy_websockets = true
        # Anyone in the tenant. Narrow this (or add an Entra group assignment on the app) if the
        # tenant is bigger than the set of people who should get a shell on this box — and it is a
        # shell: T3 drives a coding agent with write access to whatever the VM can reach.
        email_domains = ["*"]
        O2PCFG

        # ---------------------------------------------------------------- systemd: t3
        sudo tee /etc/systemd/system/t3.service >/dev/null <<T3UNIT
        [Unit]
        Description=T3 Code server
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=simple
        User={{o.RemoteUser}}
        WorkingDirectory={{o.RemoteHome}}
        # --host 127.0.0.1 is load-bearing: the only authentication in front of t3 is oauth2-proxy,
        # so a t3 reachable on the public interface is a t3 with no Entra gate at all.
        ExecStart=/usr/bin/env t3 serve --host 127.0.0.1 --port {{T3Port}}
        Restart=always
        RestartSec=5
        Environment=NODE_ENV=production
        # codex is spawned *by t3*, so it inherits this unit's environment -- and env_key in
        # ~/.codex/config.toml names PKS_CODEX_TOKEN. Without this line every codex request to the
        # passthrough goes out unauthenticated and the box looks healthy while Foundry is dead.
        # The leading dash is required: phase 1 enables this unit before the file is delivered.
        # (No backticks in this heredoc -- it is unquoted, so they would run as commands.)
        EnvironmentFile=-/etc/pks-t3/foundry.env

        [Install]
        WantedBy=multi-user.target
        T3UNIT

        # ---------------------------------------------------------------- systemd: foundry passthrough
        # `pks codex` starts this passthrough in-process and it dies with the command. Here t3 spawns
        # codex, not pks, so the passthrough has to outlive any single invocation — hence a unit.
        sudo tee /etc/systemd/system/pks-foundry-proxy.service >/dev/null <<FPUNIT
        [Unit]
        Description=pks Foundry passthrough for Codex
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=simple
        User={{o.RemoteUser}}
        WorkingDirectory={{o.RemoteHome}}
        EnvironmentFile=/etc/pks-t3/foundry.env
        ExecStart=/usr/bin/env pks foundry proxy --port {{FoundryProxyPort}} --token \${PKS_FOUNDRY_PROXY_TOKEN}
        Restart=always
        RestartSec=5

        [Install]
        WantedBy=multi-user.target
        FPUNIT

        # ---------------------------------------------------------------- systemd: oauth2-proxy
        sudo tee /etc/systemd/system/oauth2-proxy.service >/dev/null <<O2PUNIT
        [Unit]
        Description=oauth2-proxy (Entra ID) in front of T3 Code
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=simple
        User=root
        EnvironmentFile=/etc/pks-t3/oauth2-proxy.env
        ExecStart=/usr/local/bin/oauth2-proxy --config=/etc/pks-t3/oauth2-proxy.cfg
        Restart=always
        RestartSec=5

        [Install]
        WantedBy=multi-user.target
        O2PUNIT

        # ---------------------------------------------------------------- caddy site
        sudo tee /etc/caddy/Caddyfile >/dev/null <<CADDY
        {
        	email {{acmeEmail}}
        }

        {{o.Domain}} {
        	encode zstd gzip
        	reverse_proxy 127.0.0.1:{{OAuthProxyPort}}
        }
        CADDY

        # ---------------------------------------------------------------- codex -> foundry
        # The provider block Codex reads. `pks codex` writes the equivalent at launch time against a
        # random port; this one is static because the passthrough is now a service on a fixed port.
        install -d -m 0700 {{o.RemoteHome}}/.codex
        cat > {{o.RemoteHome}}/.codex/config.toml <<CODEX
        model = "{{o.FoundryDeployment}}"
        model_provider = "pks-foundry"

        [model_providers.pks-foundry]
        name = "Azure AI Foundry (via pks)"
        base_url = "http://127.0.0.1:{{FoundryProxyPort}}/openai/v1"
        wire_api = "responses"
        env_key = "PKS_CODEX_TOKEN"
        CODEX

        sudo systemctl daemon-reload
        sudo systemctl enable --now t3.service caddy.service
        echo "==> pks t3: phase 1 done (t3 + caddy up; oauth2-proxy waits for its secret)"
        """;
    }

    /// <summary>
    /// Phase 2 — the credentials, read from stdin.
    ///
    /// The command itself carries no secret; it is a <c>cat &gt; file</c> with a chmod. What goes down
    /// the pipe is written by <c>SecretSink</c> so the command layer never holds the plaintext, and
    /// what lands on the box is 0600 root-owned under <c>/etc/pks-t3</c>.
    /// </summary>
    public static string SecretDeliveryScript() => """
        set -euo pipefail
        umask 077
        sudo install -d -m 0750 -o root -g root /etc/pks-t3
        sudo tee /etc/pks-t3/oauth2-proxy.env >/dev/null
        sudo chmod 0600 /etc/pks-t3/oauth2-proxy.env
        sudo chown root:root /etc/pks-t3/oauth2-proxy.env
        sudo systemctl enable --now oauth2-proxy.service
        sudo systemctl restart oauth2-proxy.service
        systemctl is-active oauth2-proxy.service
        """;

    /// <summary>
    /// Phase 2b — the Foundry passthrough token, same stdin discipline.
    ///
    /// Separate from the oauth2-proxy env file on purpose: this one is owned by the VM user (the
    /// passthrough runs as them), the other by root, and merging them would mean the T3 user could
    /// read the Entra client secret.
    /// </summary>
    public static string FoundryTokenDeliveryScript(string remoteUser) => $"""
        set -euo pipefail
        umask 077
        sudo install -d -m 0750 -o root -g root /etc/pks-t3
        sudo tee /etc/pks-t3/foundry.env >/dev/null
        sudo chmod 0640 /etc/pks-t3/foundry.env
        sudo chown root:{remoteUser} /etc/pks-t3/foundry.env
        # Both units read this file, and both are already running by the time it lands (or is
        # rewritten by --rotate). try-restart, not restart: on the first pass the passthrough is not
        # enabled yet and restart would fail the script.
        sudo systemctl try-restart t3.service pks-foundry-proxy.service
        """;
}

/// <summary>Everything <see cref="T3BootstrapScript.Build"/> needs that is not a secret.</summary>
public sealed record T3BootstrapOptions
{
    public required string Domain { get; init; }
    public required string TenantId { get; init; }
    public required string RemoteUser { get; init; }
    public required string RemoteHome { get; init; }
    public required string FoundryDeployment { get; init; }
    public string? AcmeEmail { get; init; }

    /// <summary>Pinned rather than "latest": oauth2-proxy publishes no rolling URL, and an
    /// unversioned fetch would silently stop matching the flags in the generated config.</summary>
    public string OAuth2ProxyVersion { get; init; } = "7.8.1";
}
