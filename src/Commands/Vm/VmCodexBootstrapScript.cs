namespace PKS.Commands.Vm;

/// <summary>
/// Options for <see cref="VmCodexBootstrapScript.Build"/>.
/// </summary>
public sealed class VmCodexBootstrapOptions
{
    /// <summary>The login account the Codex app server will run as. Never root — the whole point
    /// of the command is that an agent with write access to the box is not also uid 0.</summary>
    public string User { get; set; } = VmCodexBootstrapScript.DefaultUser;

    /// <summary>Public keys to authorise for <see cref="User"/>. Empty means "copy whatever the
    /// account we are connecting as already trusts", which is strictly less access than the
    /// connecting account itself has.</summary>
    public IReadOnlyList<string> AuthorizedKeys { get; set; } = Array.Empty<string>();

    /// <summary>NodeSource major to install when the box's node is older. Codex itself declares
    /// <c>engines.node &gt;= 16</c>, but Ubuntu LTS ships far below that and the app server is
    /// happier on a current LTS.</summary>
    public int NodeMajor { get; set; } = VmCodexBootstrapScript.DefaultNodeMajor;

    /// <summary>Add the account to the <c>docker</c> group. Docker group membership is root
    /// equivalence on the host, so it is opt-in and the summary says so.</summary>
    public bool Docker { get; set; }

    /// <summary>Grant passwordless sudo. Opt-in for the same reason as <see cref="Docker"/>:
    /// the default has to be the thing the user asked for, which was "not root".</summary>
    public bool Sudo { get; set; }

    /// <summary>Pin the npm version instead of taking <c>@latest</c>.</summary>
    public string? CodexVersion { get; set; }
}

/// <summary>
/// The bash that makes a box answer Codex Desktop's "Remote SSH" connection: a current node, the
/// Codex CLI on the global PATH, and a dedicated unprivileged login account to run it as.
///
/// Generated rather than shipped as a file for the same reason as
/// <see cref="PKS.Commands.T3.T3BootstrapScript"/>: the account name, the authorised keys and the
/// node major are only known at call time. Nothing here is a secret — public keys are public — so
/// unlike the T3 script every value may safely appear in an ssh argv.
///
/// Three constraints come from how Codex Desktop actually connects, and getting any of them wrong
/// produces a box that works perfectly over ssh and still reads "Disconnected" in the app:
///
/// 1. The app starts <c>codex app-server</c> <b>through the account's login shell</b>. So the
///    account needs a real shell and a real home — a <c>--system … --shell /usr/sbin/nologin</c>
///    service account of the kind T3 creates for its Foundry passthrough cannot host Codex.
/// 2. <c>codex</c> has to be on that login shell's PATH. Installing globally as root puts it in
///    <c>/usr/bin</c>, which every login shell has; a per-user <c>npm install</c> under
///    <c>~/.local/bin</c> would not survive the app's non-interactive probe on every box.
/// 3. Authentication is per-account and cannot be done from here. <c>codex login</c> needs a
///    browser callback or a device code, so the script deliberately stops once the binary answers
///    and leaves <see cref="LoginHint"/> to the caller to print.
///
/// Every step is idempotent: re-running against a box that already has Codex is the supported way
/// to upgrade the CLI or add a key, so no step may append where it means to replace.
/// </summary>
public static class VmCodexBootstrapScript
{
    /// <summary>The account name used when the caller does not pick one.</summary>
    public const string DefaultUser = "codex";

    /// <summary>NodeSource major installed when the box's node is too old.</summary>
    public const int DefaultNodeMajor = 22;

    /// <summary>The same value as text, because an attribute argument has to be a constant
    /// expression and an interpolated <c>int</c> const is not one. Pinned to
    /// <see cref="DefaultNodeMajor"/> by a test so the help text cannot drift away from the default
    /// it claims to document.</summary>
    public const string DefaultNodeMajorText = "22";

    /// <summary>The npm package that provides the <c>codex</c> binary.</summary>
    public const string Package = "@openai/codex";

    /// <summary>
    /// The port Codex Desktop's managed remote app server binds on the box. One Codex account per
    /// host: a second one collides here, and the reported symptom is the ugly one — attaching to
    /// the other account's running server and showing its configuration (openai/codex#19590). The
    /// script warns rather than refuses, because two accounts on one box is a legitimate thing to
    /// set up deliberately.
    /// </summary>
    public const int AppServerPort = 9234;

    /// <summary>
    /// The one step that cannot be automated from here, worded for the box it was run against.
    /// The browser flow's callback lands on the <em>box's</em> loopback, so on a headless host it
    /// needs either a device code or a forwarded port.
    /// </summary>
    public static string LoginHint(string user, string host, int port)
    {
        var p = port == 22 ? string.Empty : $" -p {port}";
        return $"ssh{p} {user}@{host} -t codex login --device-auth";
    }

    /// <summary>The ssh config stanza Codex Desktop needs in order to offer the box at all: it
    /// reads concrete <c>Host</c> aliases from <c>~/.ssh/config</c> and ignores pattern-only ones,
    /// and the alias has to carry the new account or the app connects as whoever it did before.</summary>
    public static string SshConfigStanza(string alias, string host, string user, int port, string? identityFile)
    {
        var lines = new List<string>
        {
            $"Host {alias}",
            $"    HostName {host}",
            $"    User {user}",
        };
        if (port != 22) lines.Add($"    Port {port}");
        // A placeholder rather than nothing when we do not know the path. The stanza is for the
        // user's own laptop, and the key pks used to connect from here is frequently not a file that
        // exists over there at all — a pks-managed key never is. Codex on Windows then cannot find
        // an identity from a stanza that names none, and the failure looks like a refused host.
        lines.Add(string.IsNullOrWhiteSpace(identityFile)
            ? $"    IdentityFile <the key you already use for {user}@{host} — same key as the account you bootstrapped from>"
            : $"    IdentityFile {identityFile}");
        lines.Add("    IdentitiesOnly yes");
        return string.Join('\n', lines);
    }

    public static string Build(VmCodexBootstrapOptions o)
    {
        var spec = string.IsNullOrWhiteSpace(o.CodexVersion) ? $"{Package}@latest" : $"{Package}@{o.CodexVersion}";

        // The conditional sections are assembled here, at column 0, rather than inlined into the
        // literal below. A here-doc delimiter has to start at column 0 in the *rendered* script, and
        // a raw string literal only re-indents its own text — never the text interpolation drops
        // into it. Building them separately is the difference between a working here-doc and a
        // remote shell that hangs waiting for a terminator it will never see.
        var keysSection = BuildKeysSection(o.AuthorizedKeys);
        var dockerSection = o.Docker ? DockerSection : "# docker group not requested\n";
        var sudoSection = o.Sudo ? SudoGrantSection : SudoRevokeSection;

        return $$"""
        set -euo pipefail

        # set -e aborts with no indication of where, and apt's debconf chatter then fills the tail
        # of stderr, so without this the caller prints noise and the failing line has scrolled away.
        trap 'echo "pks vm codex: FAILED at line $LINENO: $BASH_COMMAND" >&2' ERR

        CODEX_USER={{o.User}}
        echo "==> pks vm codex: preparing $CODEX_USER on $(hostname)"

        export DEBIAN_FRONTEND=noninteractive

        # Root over ssh is the common case on these boxes; a non-root login needs sudo that does not
        # ask, because there is no tty on the other end of this pipe to answer with.
        #
        # A function, not a SUDO="" variable. The variable form looks equivalent and is not: bash
        # decides what is an assignment prefix and what is a command name while *parsing*, before the
        # variable expands. So a privileged `FOO=bar apt-get ...` with an empty prefix runs `FOO=bar`
        # as the command and dies with "command not found", and a piped `| -E bash -` runs `-E`. Both
        # forms work under sudo and break as root -- which is the common case here, so they would
        # break in exactly the configuration they are most used in. Measured in an ubuntu:24.04
        # container, 2026-11-02.
        if [ "$(id -u)" -eq 0 ]; then
          pks_run() { "$@"; }
        else
          sudo -n true 2>/dev/null || { echo "pks vm codex: need root or passwordless sudo on this host" >&2; exit 78; }
          pks_run() { sudo -n "$@"; }
        fi

        # ---------------------------------------------------------------- packages
        pks_run apt-get update -qq
        pks_run env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq curl ca-certificates

        # ---------------------------------------------------------------- node
        # Replace only what is too old. A box that already carries a newer node than we would
        # install — a devbox, a runner image — must not be downgraded under the user's feet.
        # Compared numerically, not by regex: a regex over the major is what makes this step quietly
        # wrong for one value of --node and right for the rest.
        #
        # The `command -v` guard is not decoration. Assigning from a command substitution adopts its
        # exit status, so under `set -e` + `pipefail` a box with no node at all — the ordinary fresh
        # box, and the whole point of the command — aborted the script here. Found by running this
        # script in an ubuntu:24.04 container, 2026-11-02.
        CURRENT_NODE=""
        if command -v node >/dev/null 2>&1; then
          CURRENT_NODE=$(node --version | sed -n 's/^v\([0-9]\{1,\}\).*/\1/p')
        fi
        if [ -z "$CURRENT_NODE" ] || [ "$CURRENT_NODE" -lt {{o.NodeMajor}} ]; then
          echo "==> installing node {{o.NodeMajor}} from NodeSource"
          curl -fsSL https://deb.nodesource.com/setup_{{o.NodeMajor}}.x | pks_run env DEBIAN_FRONTEND=noninteractive bash -
          pks_run env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq nodejs
        fi
        node --version

        # ---------------------------------------------------------------- codex CLI
        # Installed globally, as root, on purpose: npm's global prefix is /usr here, so the binary
        # lands in /usr/bin and is therefore on the PATH of every *login* shell. Codex Desktop
        # probes `codex --version` through the account's login shell and nothing else, so a
        # per-user install under ~/.local/bin is the failure mode this line exists to avoid.
        pks_run npm install -g --silent {{spec}}
        command -v codex
        codex --version

        # ---------------------------------------------------------------- the account
        # A real shell and a real home, NOT a --system/nologin service account: the app server is
        # started through this account's login shell.
        if ! id -u "$CODEX_USER" >/dev/null 2>&1; then
          echo "==> creating $CODEX_USER"
          # -p '*' and not the useradd default. Left alone, useradd writes "!" into the shadow
          # password field, which means LOCKED, and sshd rejects a locked account outright --
          # before it ever looks at authorized_keys, so a perfectly good key is refused with a bare
          # "Permission denied (publickey)" and the box's own log says "not allowed because account
          # is locked". Measured 2026-11-02 against a real sshd.
          #
          # Cloud images get away with "!" because they set UsePAM yes, which delegates the decision
          # and lets key-only accounts through; a minimal or hardened sshd_config does not, and that
          # is exactly the kind of box this command is pointed at. "*" is the portable answer: no
          # password can ever match it, so it grants nothing, but it is not the locked flag.
          pks_run useradd --create-home --shell /bin/bash --password '*' --comment "Codex remote agent" "$CODEX_USER"
        fi

        # The same repair for an account an earlier run of this script created, or one someone made
        # by hand with a plain useradd. Only the never-set values are touched: a real hash is
        # somebody's deliberate password, and "!<hash>" is an account an admin locked on purpose.
        SHADOW_PW=$(pks_run getent shadow "$CODEX_USER" 2>/dev/null | cut -d: -f2 || true)
        if [ "$SHADOW_PW" = "!" ] || [ "$SHADOW_PW" = "!!" ]; then
          echo "pks vm codex: $CODEX_USER's password field is locked ('$SHADOW_PW') — sshd refuses a locked account even with a valid key, so clearing it to '*'" >&2
          pks_run usermod --password '*' "$CODEX_USER"
        fi

        # Ask for the home rather than assuming /home/$CODEX_USER: useradd honours
        # /etc/default/useradd, and a pre-existing account may live anywhere at all.
        CODEX_HOME=$(getent passwd "$CODEX_USER" | cut -d: -f6)
        [ -n "$CODEX_HOME" ] || { echo "pks vm codex: $CODEX_USER has no home directory" >&2; exit 1; }
        CODEX_SHELL=$(getent passwd "$CODEX_USER" | cut -d: -f7)
        case "$CODEX_SHELL" in
          *nologin|*false|"")
            echo "pks vm codex: $CODEX_USER's shell is '$CODEX_SHELL' — Codex starts its app server through the login shell, so fixing it" >&2
            pks_run usermod --shell /bin/bash "$CODEX_USER"
            ;;
        esac

        pks_run install -d -m 0700 -o "$CODEX_USER" -g "$CODEX_USER" "$CODEX_HOME/.ssh"
        # ~/.codex holds auth.json once the account logs in. Created now, with the tight mode, so
        # the credential is never written into a world-readable directory.
        pks_run install -d -m 0700 -o "$CODEX_USER" -g "$CODEX_USER" "$CODEX_HOME/.codex"

        # ---------------------------------------------------------------- authorized keys
        # Written to a temp file and moved into place: sshd opens this file on every attempt, so a
        # truncate-then-write leaves a window in which the account cannot be logged into at all.
        AK="$CODEX_HOME/.ssh/authorized_keys"
        TMP_AK="$CODEX_HOME/.ssh/.authorized_keys.pks"
        {{keysSection}}
        pks_run chown "$CODEX_USER:$CODEX_USER" "$TMP_AK"
        pks_run chmod 0600 "$TMP_AK"
        pks_run mv "$TMP_AK" "$AK"
        echo "==> authorized_keys: $(pks_run grep -c '^ssh-\|^ecdsa-\|^sk-' "$AK" || true) key(s)"

        # ---------------------------------------------------------------- group membership
        {{dockerSection}}
        {{sudoSection}}
        # ---------------------------------------------------------------- sshd will actually let it in
        # A box with AllowUsers/AllowGroups rejects the new account however correct its keys are,
        # and the app reports that as a plain connection failure. Warn; do not edit sshd_config,
        # because a bad edit there locks everyone out of the box.
        if pks_run sshd -T 2>/dev/null | grep -qE '^(allowusers|allowgroups) '; then
          if ! pks_run sshd -T 2>/dev/null | grep -qE "^(allowusers|allowgroups) .*(^| )$CODEX_USER( |$)"; then
            echo "pks vm codex: sshd has AllowUsers/AllowGroups set and $CODEX_USER is not listed — add it or the app cannot connect" >&2
          fi
        fi

        # ---------------------------------------------------------------- the acceptance test
        # Exactly what Codex Desktop does: reach the binary through the account's LOGIN shell. A
        # `command -v codex` as root proves nothing about what the app will find.
        # `su -` proves the binary is on the login shell's PATH, which is what Codex probes. It
        # cannot prove sshd will admit the account -- su bypasses sshd entirely -- so the locked
        # password field above is checked separately rather than waited for here.
        echo "==> probing codex through $CODEX_USER's login shell"
        pks_run su - "$CODEX_USER" -c 'command -v codex && codex --version'

        # How this account will authenticate, which is one of two entirely different things.
        #
        # A custom provider is configured, not signed in: config.toml names a model_provider and
        # carries the credential as a header, so `codex login` is not a step that exists. Reporting
        # "NOT signed in" for such a box would send the reader after a login that cannot help, and
        # for the gateway case it is actively wrong. So the config is checked first.
        CODEX_CFG="$CODEX_HOME/.codex/config.toml"
        if pks_run test -f "$CODEX_CFG" && pks_run grep -qE '^[[:space:]]*model_provider[[:space:]]*=' "$CODEX_CFG"; then
          CODEX_PROVIDER=$(pks_run sed -n 's/^[[:space:]]*model_provider[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' "$CODEX_CFG" | head -1)

          # env_http_headers names a variable that has to be in the environment of whatever starts
          # codex. Over ssh that is a non-interactive shell, which reads neither ~/.bashrc (guarded
          # on the first line by Ubuntu's `case $- in *i*)` test) nor, unless it is a login shell,
          # ~/.profile. Measured on 2026-11-02: both empty for `ssh box cmd`. A key placed in a
          # shell profile is therefore present for a human at a prompt and absent for the app
          # server, and the gateway answers 401 — which reads as a bad key, not a missing variable.
          # http_headers carries the value in the file instead and has no such dependency.
          if pks_run grep -q 'env_http_headers' "$CODEX_CFG"; then
            echo "pks vm codex: $CODEX_CFG uses env_http_headers — the variable it names will be EMPTY for the app server started over ssh (non-interactive shell reads no profile). Use http_headers for a remote box." >&2
          fi

          echo "==> provider configured: ${CODEX_PROVIDER:-unknown} (no codex login needed)"
        elif pks_run su - "$CODEX_USER" -c 'codex login status' >/dev/null 2>&1; then
          # Already-authenticated is the normal state on a re-run.
          echo "==> $CODEX_USER is already signed in to Codex"
        else
          # Absent is the normal state on a first run against OpenAI, and is not an error, because
          # only a human can complete it.
          echo "==> $CODEX_USER is NOT signed in yet"
        fi

        # One Codex account per box: the managed remote app server binds a fixed port.
        if command -v ss >/dev/null 2>&1 && pks_run ss -ltn 2>/dev/null | grep -q ':{{AppServerPort}} '; then
          echo "pks vm codex: something already listens on {{AppServerPort}} — a second Codex account on this box will collide with it" >&2
        fi

        echo "==> pks vm codex: $CODEX_USER ready on $(hostname)"
        """;
    }

    /// <summary>
    /// Either the caller's keys as a quoted here-doc, or a copy of the connecting account's own
    /// <c>authorized_keys</c>. The quoted delimiter matters: a key comment can contain anything,
    /// and an unquoted here-doc would try to expand it.
    /// </summary>
    private static string BuildKeysSection(IReadOnlyList<string> keys)
    {
        var cleaned = keys.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        if (cleaned.Count == 0)
        {
            return """
            # No keys were given, so inherit the ones the account we are connecting as already
            # trusts. That grants the new account no access the connecting account did not already
            # have, and it is what lets the command work with a single argument.
            SRC="${HOME:-$(getent passwd "$(id -un)" | cut -d: -f6)}/.ssh/authorized_keys"
            [ -r "$SRC" ] || { echo "pks vm codex: no keys given and $SRC is not readable — pass --authorized-key" >&2; exit 1; }
            pks_run cp "$SRC" "$TMP_AK"
            """;
        }

        return "pks_run tee \"$TMP_AK\" >/dev/null <<'PKS_CODEX_KEYS'\n"
             + string.Join('\n', cleaned) + "\n"
             + "PKS_CODEX_KEYS";
    }

    private const string DockerSection = """
        # Opt-in, and worth being blunt about: the docker group is root on this host by another
        # name, so this undoes most of what running as a separate account bought.
        if getent group docker >/dev/null 2>&1; then
          pks_run usermod -aG docker "$CODEX_USER"
          echo "==> $CODEX_USER added to the docker group (root-equivalent on this host)"
        else
          echo "==> no docker group on this host; skipped" >&2
        fi
        """;

    private const string SudoGrantSection = """
        pks_run install -d -m 0755 /etc/sudoers.d
        printf '%s ALL=(ALL) NOPASSWD:ALL\n' "$CODEX_USER" | pks_run tee "/etc/sudoers.d/90-pks-codex-$CODEX_USER" >/dev/null
        pks_run chmod 0440 "/etc/sudoers.d/90-pks-codex-$CODEX_USER"
        echo "==> $CODEX_USER granted passwordless sudo"
        """;

    private const string SudoRevokeSection = """
        # Not granted. If a previous run granted it and the caller has now dropped --sudo, take it
        # back: leaving it would make the flag one-way, and the box's state would no longer be
        # readable from the command line that produced it.
        pks_run rm -f "/etc/sudoers.d/90-pks-codex-$CODEX_USER"
        """;
}
