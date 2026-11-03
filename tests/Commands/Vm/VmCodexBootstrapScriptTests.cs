using System;
using System.Linq;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Vm;
using Xunit;

namespace PKS.CLI.Tests.Commands.Vm;

/// <summary>
/// The script runs as root on someone's box, and its failure mode is specific: every wrong version
/// of it still produces a host you can ssh into and run <c>codex</c> on by hand, while Codex
/// Desktop goes on reporting "Disconnected". So what is pinned here is not "does it install Codex"
/// — it is the handful of properties the app depends on and a human check would not notice.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class VmCodexBootstrapScriptTests
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHtest poul@laptop";

    private static VmCodexBootstrapOptions Options() => new();

    [Fact]
    public void Account_gets_a_real_login_shell_and_a_real_home()
    {
        // The reason this is a test and not a comment: the sibling T3 script creates its service
        // account with `--system --shell /usr/sbin/nologin`, and copying that line here would be the
        // natural thing to do. It would also be fatal — Codex Desktop starts the remote app server
        // *through the account's login shell*, so a nologin account can never host it.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().Contain("useradd --create-home --shell /bin/bash");

        // Asserted against the useradd invocation, not the whole script: the prose above that line
        // names --system and nologin in order to explain why they are absent.
        var useradd = script.Split('\n').Single(l => l.Contains("useradd ") && !l.TrimStart().StartsWith('#'));
        useradd.Should().NotContain("--system");
        useradd.Should().NotContain("nologin");
    }

    [Fact]
    public void A_preexisting_account_with_a_nologin_shell_is_repaired()
    {
        // The box may already have a `codex` account from some other purpose. Creating it is
        // guarded by `id -u`, so without this the command would silently do nothing about the one
        // property that decides whether the app can connect.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().Contain("usermod --shell /bin/bash");
    }

    [Fact]
    public void Codex_is_installed_globally_not_per_user()
    {
        // A per-user `npm install` lands in ~/.local/bin, which a non-interactive probe through the
        // login shell does not reliably have. Global as root lands in /usr/bin, which it always has.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().Contain("pks_run npm install -g --silent @openai/codex@latest");
        script.Should().NotContain("npm install --prefix");
    }

    [Fact]
    public void Version_can_be_pinned()
    {
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions { CodexVersion = "0.166.0" });

        script.Should().Contain("@openai/codex@0.166.0");
        script.Should().NotContain("@openai/codex@latest");
    }

    [Fact]
    public void It_probes_codex_through_the_login_shell_not_as_root()
    {
        // `command -v codex` as root proves nothing about what the app will find. The probe has to
        // take the same path the app takes, or the command reports success on a box that fails.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().Contain("""pks_run su - "$CODEX_USER" -c 'command -v codex && codex --version'""");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(22)]
    [InlineData(24)]
    public void Node_is_compared_numerically_so_every_major_behaves(int major)
    {
        // An earlier draft matched the major with a regex built from the option. It was correct for
        // 22 and quietly wrong for 18 — the class of bug that only shows up on one box.
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions { NodeMajor = major });

        script.Should().Contain($"""[ "$CURRENT_NODE" -lt {major} ]""");
        script.Should().Contain($"setup_{major}.x");
    }

    [Fact]
    public void Given_keys_land_in_a_quoted_heredoc_whose_terminator_starts_the_line()
    {
        // A here-doc terminator that is indented is never recognised, and the remote shell then
        // waits for it forever — the command hangs instead of failing. The quoting matters too: a
        // key comment may contain `$` or backticks, which an unquoted here-doc would expand.
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions { AuthorizedKeys = new[] { Key } });

        script.Should().Contain("<<'PKS_CODEX_KEYS'");
        script.Should().Contain(Key);
        script.Split('\n').Should().Contain("PKS_CODEX_KEYS");
    }

    [Fact]
    public void Blank_and_whitespace_keys_do_not_become_blank_authorized_lines()
    {
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions
        {
            AuthorizedKeys = new[] { "  ", Key, "" },
        });

        var heredoc = script.Split("<<'PKS_CODEX_KEYS'")[1].Split("PKS_CODEX_KEYS")[0];
        heredoc.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0)
            .Should().ContainSingle().Which.Should().Be(Key);
    }

    [Fact]
    public void With_no_keys_it_inherits_the_connecting_accounts_own_authorized_keys()
    {
        // The one-argument case. Inheriting is safe by construction: it grants the new account no
        // access that the account we are already connected as did not have.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().Contain(".ssh/authorized_keys\"");
        script.Should().Contain("pks_run cp \"$SRC\" \"$TMP_AK\"");
        script.Should().NotContain("PKS_CODEX_KEYS");
    }

    [Fact]
    public void Authorized_keys_is_moved_into_place_never_truncated()
    {
        // sshd opens authorized_keys on every attempt, so writing it in place leaves a window in
        // which the account cannot be logged into — including by the app, mid-connection.
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions { AuthorizedKeys = new[] { Key } });

        // Trailing quote left off the needles on purpose: a raw string that ends in one needs a
        // longer delimiter, and the prefix is already unambiguous.
        script.Should().Contain("""pks_run mv "$TMP_AK" "$AK""");
        script.Should().Contain("""pks_run chmod 0600 "$TMP_AK""");
    }

    [Fact]
    public void Privilege_is_off_by_default()
    {
        // The user's stated reason for the whole feature was "so it is not root it runs as". A
        // default that hands out docker or sudo would give that back without saying so.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().NotContain("NOPASSWD");
        script.Should().NotContain("usermod -aG docker");
    }

    [Fact]
    public void Dropping_sudo_takes_it_back_rather_than_leaving_it()
    {
        // Otherwise --sudo is one-way: the box keeps a grant that the command line that produced it
        // no longer describes, and nobody can tell from the outside.
        var without = VmCodexBootstrapScript.Build(Options());
        var with = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions { Sudo = true });

        without.Should().Contain("""pks_run rm -f "/etc/sudoers.d/90-pks-codex-$CODEX_USER""");
        with.Should().Contain("NOPASSWD:ALL");
        with.Should().NotContain("rm -f \"/etc/sudoers.d/90-pks-codex");
    }

    [Fact]
    public void Docker_group_is_granted_only_when_asked_and_says_what_it_costs()
    {
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions { Docker = true });

        script.Should().Contain("usermod -aG docker");
        script.Should().Contain("root-equivalent");
    }

    [Fact]
    public void It_warns_about_sshd_allowlists_but_never_edits_sshd_config()
    {
        // AllowUsers/AllowGroups rejects the new account however correct its keys are, and the app
        // shows that as a plain connection failure. Editing sshd_config to fix it is how you lock
        // everyone out of a production box, so the script only reports it.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().Contain("allowusers|allowgroups");
        script.Should().NotContain("tee /etc/ssh/sshd_config");
        script.Should().NotContain("systemctl restart ssh");
    }

    [Fact]
    public void It_warns_when_the_fixed_app_server_port_is_already_taken()
    {
        // openai/codex#19590: the managed remote app server binds a fixed port, and a second Codex
        // account on the same box can attach to the first account's server and show its config.
        var script = VmCodexBootstrapScript.Build(Options());

        VmCodexBootstrapScript.AppServerPort.Should().Be(9234);
        script.Should().Contain($":{VmCodexBootstrapScript.AppServerPort} ");
    }

    [Fact]
    public void Privilege_is_a_function_never_an_expanded_prefix_variable()
    {
        // The bug this pins was found by running the script in an ubuntu:24.04 container as root:
        // `$SUDO FOO=bar apt-get …` with an empty $SUDO runs `FOO=bar` as the command name, because
        // bash classifies assignment prefixes while parsing, before the variable expands. Same for
        // `… | $SUDO -E bash -`, which runs `-E`. Both work under sudo and fail as root — and these
        // boxes are reached as root, so the broken form fails in its most common configuration.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().Contain("""pks_run() { "$@"; }""");
        script.Should().Contain("""pks_run() { sudo -n "$@"; }""");

        var code = script.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).ToList();
        code.Should().NotContain(l => l.Contains("$SUDO"));
        code.Should().NotContain(l => l.Contains("| pks_run -E"));

        // Environment across the privilege boundary goes through `env`, which needs neither sudo -E
        // nor an env_keep entry and is inert when we are already root.
        script.Should().Contain("pks_run env DEBIAN_FRONTEND=noninteractive apt-get");
    }

    [Fact]
    public void It_fails_fast_when_it_has_neither_root_nor_silent_sudo()
    {
        // There is no tty on the far end of this pipe, so a sudo that asks for a password is a hang.
        var script = VmCodexBootstrapScript.Build(Options());

        script.Should().Contain("sudo -n true");
        script.Should().Contain("exit 78");
        script.Should().StartWith("set -euo pipefail");
    }

    [Fact]
    public void Login_hint_uses_device_auth_because_the_browser_callback_lands_on_the_box()
    {
        // The default login flow opens a callback on the *remote* loopback, which a browser on the
        // laptop cannot reach. Device auth is the flow that works over a bare ssh session.
        VmCodexBootstrapScript.LoginHint("codex", "arvo.example.com", 22)
            .Should().Be("ssh codex@arvo.example.com -t codex login --device-auth");

        VmCodexBootstrapScript.LoginHint("codex", "arvo.example.com", 2222)
            .Should().Contain("-p 2222");
    }

    [Fact]
    public void Default_node_major_and_its_help_text_cannot_drift_apart()
    {
        // The text exists only because an attribute argument has to be a constant expression and an
        // interpolated int const is not one. Two spellings of one value is exactly what rots.
        VmCodexBootstrapScript.DefaultNodeMajorText
            .Should().Be(VmCodexBootstrapScript.DefaultNodeMajor.ToString());
    }

    [Fact]
    public void Ssh_config_stanza_is_a_concrete_alias_carrying_the_new_account()
    {
        // Codex reads concrete Host aliases from ~/.ssh/config and ignores pattern-only ones. And
        // without a new alias the app keeps connecting as whoever it connected as before — which on
        // these boxes is root, the thing the account exists to avoid.
        var stanza = VmCodexBootstrapScript.SshConfigStanza(
            "arvo-codex", "projects.kjeldager.io", "codex", 22, "~/.ssh/id_ed25519_arvo");

        stanza.Should().StartWith("Host arvo-codex\n");
        stanza.Should().Contain("    HostName projects.kjeldager.io");
        stanza.Should().Contain("    User codex");
        stanza.Should().Contain("    IdentityFile ~/.ssh/id_ed25519_arvo");
        stanza.Should().Contain("    IdentitiesOnly yes");
        stanza.Should().NotContain("*");
        stanza.Should().NotContain("Port 22");
    }

    [Fact]
    public void Stanza_names_an_identity_even_when_the_path_is_unknown()
    {
        // The key pks connected with lives on this machine; the stanza is for the user's laptop. A
        // pks-managed key has no path over there at all, and on Windows a stanza naming no identity
        // makes Codex fail in a way that reads as a refused host rather than a missing key.
        var stanza = VmCodexBootstrapScript.SshConfigStanza("box-codex", "example.com", "codex", 22, null);

        stanza.Should().Contain("    IdentityFile ");
        stanza.Should().Contain("the key you already use for codex@example.com");
        stanza.Should().Contain("    IdentitiesOnly yes");
    }

    [Fact]
    public void Ssh_config_stanza_states_a_non_default_port()
    {
        VmCodexBootstrapScript.SshConfigStanza("box", "example.com", "codex", 22001, null)
            .Should().Contain("    Port 22001");
    }

    [Fact]
    public void Provider_config_is_checked_before_sign_in_state()
    {
        // A box pointed at a custom gateway authenticates through a header in config.toml; there is
        // no login to perform. Asking `codex login status` first and reporting "NOT signed in" would
        // send the reader after a step that cannot help -- and for the gateway case is simply wrong.
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions());

        var providerCheck = script.IndexOf("model_provider[[:space:]]*=", StringComparison.Ordinal);
        var loginCheck = script.IndexOf("codex login status", StringComparison.Ordinal);

        providerCheck.Should().BeGreaterThan(0, "the script must look for a configured provider");
        loginCheck.Should().BeGreaterThan(providerCheck, "the provider branch must come first");
        script.Should().Contain("no codex login needed");
    }

    [Fact]
    public void A_remote_config_using_env_http_headers_is_warned_about()
    {
        // Measured 2026-11-02 in a container: for `ssh box cmd` both ~/.bashrc and ~/.profile are
        // unread, and for a login shell ~/.bashrc still is. A credential placed in a shell profile
        // -- which is what the house PowerShell/posix installer does on a laptop -- is therefore
        // present for a human at a prompt and EMPTY for the app server Codex Desktop starts. The
        // gateway then answers 401, which reads as a bad key rather than a missing variable, so the
        // script has to name the real cause.
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions());

        script.Should().Contain("env_http_headers");
        script.Should().Contain("http_headers for a remote box");
        script.Should().Contain("pks vm codex: $CODEX_CFG uses env_http_headers");
    }

    [Fact]
    public void The_account_is_not_left_with_a_locked_password_field()
    {
        // The failure this pins: a plain `useradd` writes "!" into the shadow password field, which
        // sshd reads as LOCKED and refuses before it looks at authorized_keys at all. Measured
        // 2026-11-02 against a real sshd: a correct key gave "Permission denied (publickey)" on the
        // client and "User codex not allowed because account is locked" on the box. Cloud images
        // hide it by setting UsePAM yes; a minimal sshd_config does not.
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions());

        var useradd = script.Split('\n')
            .First(l => l.Contains("useradd", StringComparison.Ordinal) && !l.TrimStart().StartsWith('#'));

        useradd.Should().Contain("--password '*'", "a fresh account must not be created locked");

        // And an account a previous run created locked gets repaired.
        script.Should().Contain("usermod --password '*'");
    }

    [Theory]
    [InlineData("!")]
    [InlineData("!!")]
    public void Only_a_never_set_password_field_is_repaired(string lockedValue)
    {
        // A real hash is somebody's deliberate password and "!<hash>" is an account an admin locked
        // on purpose; neither is ours to clear. Only useradd's own never-set values are.
        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions());

        script.Should().Contain($"\"$SHADOW_PW\" = \"{lockedValue}\"");
        script.Should().NotContain("usermod --password '*' \"$CODEX_USER\" || true",
            "the repair must stay inside the guard, not run unconditionally");
    }
}
