using System;
using System.IO;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Vm;
using Xunit;

namespace PKS.CLI.Tests.Commands.Vm;

/// <summary>
/// This writes into a file the user owns and hand-edits, and whose breakage is expensive: a damaged
/// ~/.ssh/config does not fail loudly, it changes which key or user ssh picks for hosts that have
/// nothing to do with Codex. So the rules under test are the conservative ones — never overwrite
/// what we did not write, never duplicate an alias ssh would resolve from somewhere else, and be a
/// no-op on a re-run.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class LocalSshConfigTests
{
    private const string Alias = "pks-vm-a1b2-codex";

    private static string Stanza(string user = "codex") => VmCodexBootstrapScript.SshConfigStanza(
        Alias, "20.1.2.3", user, 22, "/home/me/.pks-cli/keys/pks-vm-a1b2");

    [Fact]
    public void Appends_a_delimited_block_to_an_existing_config()
    {
        // Poul's real config: three hand-written hosts, Windows key paths, no trailing structure we
        // may assume. Everything already there has to come back byte for byte.
        var existing = """
        Host projects.si14agents.com
            HostName projects.si14agents.com
            User root
            IdentityFile C:\dev\hetzner-demo\id_rsa
        Host arvo
            HostName projects.kjeldager.io
            User root
            IdentityFile C:\Users\pks\.ssh\id_ed25519_arvo
            IdentitiesOnly yes
        """;

        var (content, outcome) = LocalSshConfig.Upsert(existing, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.Added);
        content.Should().StartWith(existing);
        content.Should().Contain(LocalSshConfig.BeginMarker(Alias));
        content.Should().Contain(LocalSshConfig.EndMarker(Alias));
        content.Should().Contain($"Host {Alias}");
        content.Should().Contain(@"IdentityFile C:\dev\hetzner-demo\id_rsa");
    }

    [Fact]
    public void Writing_the_same_thing_twice_changes_nothing()
    {
        // `pks vm codex` is re-runnable by design, and a run that appended a second identical block
        // every time would quietly grow this file forever.
        var (once, _) = LocalSshConfig.Upsert("Host other\n    User me\n", Alias, Stanza());
        var (twice, outcome) = LocalSshConfig.Upsert(once, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.Unchanged);
        twice.Should().Be(once);
    }

    [Fact]
    public void A_changed_stanza_replaces_our_block_in_place()
    {
        // Re-running with --user or --alias changed has to rewrite, not append: ssh resolves from
        // the first matching Host, so an appended correction would never take effect.
        var (first, _) = LocalSshConfig.Upsert(string.Empty, Alias, Stanza());
        var (second, outcome) = LocalSshConfig.Upsert(first, Alias, Stanza(user: "codex2"));

        outcome.Should().Be(SshConfigOutcome.Updated);
        second.Should().Contain("User codex2");
        second.Should().NotContain("User codex\n");
        second.Split(LocalSshConfig.BeginMarker(Alias)).Should().HaveCount(2, "the block must be replaced, not duplicated");
    }

    [Fact]
    public void Surrounding_content_survives_a_replacement()
    {
        var existing = "Host before\n    User a\n\n"
            + LocalSshConfig.BeginMarker(Alias) + "\nHost " + Alias + "\n    User codex\n"
            + LocalSshConfig.EndMarker(Alias) + "\n\nHost after\n    User b\n";

        var (content, outcome) = LocalSshConfig.Upsert(existing, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.Updated);
        content.Should().Contain("Host before");
        content.Should().Contain("Host after");
        content.Should().Contain("    User b");
    }

    [Fact]
    public void An_alias_already_defined_by_hand_is_refused_not_overwritten()
    {
        // ssh takes the first matching Host block, so appending ours would do nothing at all, and
        // editing theirs is not ours to do. Refusing and saying so is the only honest outcome.
        var existing = "Host pks-vm-a1b2-codex\n    HostName somewhere.else\n    User someone\n";

        var (content, outcome) = LocalSshConfig.Upsert(existing, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.ConflictingAlias);
        content.Should().Be(existing);
    }

    [Fact]
    public void A_conflicting_alias_is_detected_in_a_multi_name_host_line()
    {
        // `Host a b c` declares three aliases on one line, and ssh matches any of them.
        var existing = $"Host staging {Alias} prod\n    User someone\n";

        var (_, outcome) = LocalSshConfig.Upsert(existing, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.ConflictingAlias);
    }

    [Fact]
    public void A_wildcard_host_is_not_a_conflict()
    {
        // `Host *` matching everything is the normal state of a real config, and Codex ignores
        // pattern-only hosts anyway. Treating it as a conflict would block every write.
        var existing = "Host *\n    ServerAliveInterval 60\n    AddKeysToAgent yes\n";

        var (content, outcome) = LocalSshConfig.Upsert(existing, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.Added);
        content.Should().Contain("ServerAliveInterval 60");
    }

    [Fact]
    public void A_half_edited_block_is_left_alone()
    {
        // A begin marker with no end means somebody edited inside our block. Rewriting from the
        // marker to end-of-file would eat whatever they put after it.
        var existing = LocalSshConfig.BeginMarker(Alias) + "\nHost " + Alias + "\n    User codex\n\nHost later\n    User b\n";

        var (content, outcome) = LocalSshConfig.Upsert(existing, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.ConflictingAlias);
        content.Should().Be(existing);
    }

    [Fact]
    public void An_empty_config_does_not_start_with_blank_lines()
    {
        var (content, outcome) = LocalSshConfig.Upsert(string.Empty, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.Added);
        content.Should().StartWith(LocalSshConfig.BeginMarker(Alias));
        content.Should().EndWith("\n");
    }

    [Fact]
    public void Crlf_input_does_not_produce_a_mangled_file()
    {
        // A Windows-authored config is CRLF throughout, and this is a Windows-first feature.
        var existing = "Host arvo\r\n    User root\r\n";

        var (content, outcome) = LocalSshConfig.Upsert(existing, Alias, Stanza());

        outcome.Should().Be(SshConfigOutcome.Added);

        // What the user wrote comes back byte for byte, and our block adopts the file's own ending
        // rather than converting the whole file to LF — which would turn a two-line addition into a
        // diff against every line they ever wrote.
        content.Should().StartWith("Host arvo\r\n    User root\r\n");
        content.Should().Contain($"Host {Alias}\r\n");
        content.Should().Contain(LocalSshConfig.BeginMarker(Alias) + "\r\n");

        // No mixed endings: every LF in the result is part of a CRLF.
        content.Replace("\r\n", string.Empty).Should().NotContain("\n");

        // And it is still idempotent on a CRLF file.
        LocalSshConfig.Upsert(content, Alias, Stanza()).Outcome.Should().Be(SshConfigOutcome.Unchanged);
    }

    [Fact]
    public void An_lf_config_stays_lf()
    {
        var (content, _) = LocalSshConfig.Upsert("Host arvo\n    User root\n", Alias, Stanza());

        content.Should().NotContain("\r");
    }

    [Fact]
    public void Write_creates_the_directory_and_the_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pks-sshcfg-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, ".ssh", "config");
        try
        {
            LocalSshConfig.Write(path, Alias, Stanza()).Should().Be(SshConfigOutcome.Added);
            File.ReadAllText(path).Should().Contain($"Host {Alias}");

            // And re-running is a no-op rather than a second block.
            LocalSshConfig.Write(path, Alias, Stanza()).Should().Be(SshConfigOutcome.Unchanged);

            if (!OperatingSystem.IsWindows())
            {
                // ssh refuses a group- or world-readable config with "Bad owner or permissions",
                // which would break every ssh from this machine, not just Codex.
                var mode = File.GetUnixFileMode(path);
                mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Default_path_is_the_one_openssh_and_codex_read()
    {
        LocalSshConfig.DefaultPath().Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config"));
    }
}
