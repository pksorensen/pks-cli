using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Vm;
using Xunit;

namespace PKS.CLI.Tests.Commands.Vm;

/// <summary>
/// The rewrite is the part of <c>pks vm codex sync-config</c> that can be wrong in a way no amount
/// of ssh debugging would reveal: every wrong version still produces a box with a plausible
/// <c>config.toml</c> on it, and the only symptom is a gateway 401 that reads as a bad key. So the
/// fixture here is not invented — it is the shape <c>gatewayCodexScripts.ts</c> in the ContextOne
/// repository actually emits, two customers and the MCP table included, because the single-customer
/// case is the one that accidentally passes.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class CodexConfigSyncTests
{
    private const string RemoteDir = "/home/codex/.codex";

    /// <summary>
    /// What the installer leaves on a Windows laptop for somebody on two customers. Note the three
    /// separate <c>env_http_headers</c> lines: one per provider block and one in the MCP table. A
    /// rewrite that handles the first and stops is the bug this fixture exists to catch.
    /// </summary>
    private const string InstallerConfig = """
        model = "model-auto-eu"
        model_provider = "acme-corp"
        model_catalog_json = "C:/Users/poul/.codex/models.acme-corp.azure.json"
        model_reasoning_effort = "medium"

        [features]
        enable_mcp_apps = true

        # Acme Corp (customsolutions). Switch to it with: codex -p acme-corp
        [model_providers.acme-corp]
        name = "Acme Corp"
        base_url = "https://apim-foundry-prod-sc.azure-api.net/customsolutions/openai/v1"
        wire_api = "responses"
        env_http_headers = { "Ocp-Apim-Subscription-Key" = "FOUNDRY_KEY_ACME_CORP" }

        # Beta Industries (customsolutions). Switch to it with: codex -p beta
        [model_providers.beta]
        name = "Beta Industries"
        base_url = "https://apim-foundry-prod-sc.azure-api.net/customsolutions/openai/v1"
        wire_api = "responses"
        env_http_headers = { "Ocp-Apim-Subscription-Key" = "FOUNDRY_KEY_BETA" }

        # Home: picks the project a new piece of work belongs to.
        [mcp_servers.home]
        url = "https://apim-foundry-prod-sc.azure-api.net/customsolutions/mcp"
        env_http_headers = { "Ocp-Apim-Subscription-Key" = "FOUNDRY_KEY_ACME_CORP" }
        """;

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    private static Func<string, string?> AllKeys() => Env(
        ("FOUNDRY_KEY_ACME_CORP", "acme-secret-0123"),
        ("FOUNDRY_KEY_BETA", "beta-secret-4567"));

    // ---------------------------------------------------------------- the header rewrite

    [Fact]
    public void Every_env_http_headers_becomes_a_literal_http_headers()
    {
        var result = CodexConfigSync.Rewrite(InstallerConfig, AllKeys(), RemoteDir);

        result.Ok.Should().BeTrue();
        // Not "contains none": the count is the point. Two provider blocks and one MCP table.
        result.Text.Split('\n').Count(l => l.Contains("http_headers")).Should().Be(3);
        result.Text.Should().NotContain("env_http_headers",
            "a variable named in the file is empty for the app server started over ssh");
        result.Text.Should().Contain("http_headers = { \"Ocp-Apim-Subscription-Key\" = \"acme-secret-0123\" }");
        result.Text.Should().Contain("http_headers = { \"Ocp-Apim-Subscription-Key\" = \"beta-secret-4567\" }");
    }

    [Fact]
    public void A_variable_this_machine_does_not_have_is_fatal_and_named()
    {
        var result = CodexConfigSync.Rewrite(
            InstallerConfig, Env(("FOUNDRY_KEY_ACME_CORP", "acme-secret-0123")), RemoteDir);

        result.Ok.Should().BeFalse("an empty value inlined produces exactly the 401 we are avoiding");
        result.MissingVariables.Should().ContainSingle().Which.Should().Be("FOUNDRY_KEY_BETA");
    }

    [Fact]
    public void An_env_http_headers_shape_we_do_not_understand_is_refused_not_passed_through()
    {
        // A reader who hand-edited their config into a multi-line table. Valid TOML, and not
        // something this rewrite can safely guess at -- so it must stop, because the failure of
        // passing it through is invisible until the gateway answers 401.
        const string handEdited = """
            model_provider = "acme-corp"

            [model_providers.acme-corp]
            env_http_headers = {
              "Ocp-Apim-Subscription-Key" = "FOUNDRY_KEY_ACME_CORP"
            }
            """;

        var result = CodexConfigSync.Rewrite(handEdited, AllKeys(), RemoteDir);

        result.Ok.Should().BeFalse();
        result.Refusals.Should().NotBeEmpty();
        result.Text.Should().Contain("env_http_headers", "the file is reported, not silently mangled");
    }

    [Fact]
    public void A_header_value_containing_quotes_is_escaped()
    {
        const string config = """
            [model_providers.acme-corp]
            env_http_headers = { "Ocp-Apim-Subscription-Key" = "KEY_VAR" }
            """;

        var result = CodexConfigSync.Rewrite(config, Env(("KEY_VAR", "we\"ird\\key")), RemoteDir);

        result.Ok.Should().BeTrue();
        result.Text.Should().Contain("\"we\\\"ird\\\\key\"");
    }

    [Fact]
    public void Variable_names_are_reported_so_the_console_never_has_to_hold_a_value()
    {
        var result = CodexConfigSync.Rewrite(InstallerConfig, AllKeys(), RemoteDir);

        result.ResolvedVariables.Should().Contain("FOUNDRY_KEY_ACME_CORP");
        result.ResolvedVariables.Should().Contain("FOUNDRY_KEY_BETA");
        string.Join(" ", result.ResolvedVariables).Should().NotContain("secret");
    }

    // ---------------------------------------------------------------- the catalogue path

    [Fact]
    public void A_windows_catalogue_path_is_repointed_at_the_remote_account()
    {
        var result = CodexConfigSync.Rewrite(InstallerConfig, AllKeys(), RemoteDir);

        result.Text.Should().Contain($"model_catalog_json = \"{RemoteDir}/models.acme-corp.azure.json\"");
        result.Text.Should().NotContain("C:/Users",
            "a Windows path names nothing on the box, and Codex's model picker is then empty");
    }

    [Fact]
    public void A_profile_catalogue_path_is_rewritten_too()
    {
        // The installer writes model_catalog_json into every <customer>.config.toml as well, and a
        // rewrite that only looks at config.toml leaves `codex -p beta` pointing at a Windows path.
        const string profile = """
            model = "model-auto-eu"
            model_provider = "beta"
            model_catalog_json = "C:\Users\poul\.codex\models.beta.azure.json"
            """;

        var result = CodexConfigSync.Rewrite(profile, AllKeys(), RemoteDir);

        result.Ok.Should().BeTrue();
        result.Text.Should().Contain($"model_catalog_json = \"{RemoteDir}/models.beta.azure.json\"");
    }

    // ---------------------------------------------------------------- file hygiene

    [Fact]
    public void A_bom_is_stripped_and_line_endings_are_normalised()
    {
        // PowerShell 5.1's Set-Content writes UTF-8 with a BOM, and Codex reads config.toml as plain
        // UTF-8: those three bytes land inside the first key name and the whole file fails to parse.
        var local = "\uFEFFmodel = \"model-auto-eu\"\r\nmodel_provider = \"acme-corp\"\r\n";

        var result = CodexConfigSync.Rewrite(local, AllKeys(), RemoteDir);

        result.Text.Should().StartWith("model = ");
        result.Text.Should().NotContain("\r");
        CodexConfigSync.ToBytes(result.Text).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    [Fact]
    public void A_loopback_base_url_is_warned_about_but_does_not_stop_the_transfer()
    {
        // The tenant emulator runs on 127.0.0.1:18432. A config copied from a laptop that used it
        // produces connection refused on the box, which also reads as a credential problem.
        const string config = """
            [model_providers.acme-corp]
            base_url = "http://127.0.0.1:18432/customsolutions/openai/v1"
            env_http_headers = { "Ocp-Apim-Subscription-Key" = "FOUNDRY_KEY_ACME_CORP" }
            """;

        var result = CodexConfigSync.Rewrite(config, AllKeys(), RemoteDir);

        result.Ok.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Contain("loopback");
    }

    [Fact]
    public void A_config_with_nothing_to_rewrite_is_passed_through_unchanged()
    {
        // An OpenAI-authenticated config has no provider block at all. Nothing here should invent
        // one, and nothing should refuse it either.
        const string plain = "model = \"gpt-5.6\"\napproval_policy = \"on-request\"\n";

        var result = CodexConfigSync.Rewrite(plain, AllKeys(), RemoteDir);

        result.Ok.Should().BeTrue();
        result.Text.Should().Be(plain);
        result.ResolvedVariables.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- discovery

    [Theory]
    [InlineData("config.toml", CodexSyncKind.Config)]
    [InlineData("acme-corp.config.toml", CodexSyncKind.Profile)]
    [InlineData("models.acme-corp.azure.json", CodexSyncKind.Catalogue)]
    public void The_three_kinds_of_file_are_recognised(string name, CodexSyncKind expected)
        => CodexConfigSync.Classify(name).Should().Be(expected);

    [Theory]
    [InlineData("auth.json")]
    [InlineData("history.jsonl")]
    [InlineData("config.toml.bak-20261103")]
    [InlineData("version.json")]
    [InlineData("notes.txt")]
    public void Everything_else_is_left_behind(string name)
        => CodexConfigSync.Classify(name).Should().BeNull();

    [Fact]
    public void Auth_json_is_never_a_candidate()
    {
        // Deliberate, and the reason is not file size. auth.json is an OpenAI credential: copying it
        // would sign the box into a ChatGPT account and clear the app's "logged out" banner for a
        // reason that has nothing to do with whether the gateway config works -- which is exactly
        // the confusion this command exists to end.
        var dir = Path.Combine(Path.GetTempPath(), "pks-codex-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.toml"), "model = \"x\"\n");
            File.WriteAllText(Path.Combine(dir, "auth.json"), "{\"tokens\":{}}");
            File.WriteAllText(Path.Combine(dir, "history.jsonl"), "{}\n");

            var items = CodexConfigSync.Discover(dir);

            items.Select(i => i.Name).Should().BeEquivalentTo(new[] { "config.toml" });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Config_toml_is_written_last_because_it_names_the_other_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pks-codex-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.toml"), "model = \"x\"\n");
            File.WriteAllText(Path.Combine(dir, "acme.config.toml"), "model = \"x\"\n");
            File.WriteAllText(Path.Combine(dir, "models.acme.azure.json"), "[]");

            var items = CodexConfigSync.Discover(dir);

            items.Select(i => i.Kind).Should().ContainInOrder(
                CodexSyncKind.Catalogue, CodexSyncKind.Profile, CodexSyncKind.Config);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_missing_local_codex_directory_is_empty_not_an_exception()
        => CodexConfigSync.Discover(Path.Combine(Path.GetTempPath(), "pks-codex-absent-" + Guid.NewGuid().ToString("N")))
            .Should().BeEmpty();
}
