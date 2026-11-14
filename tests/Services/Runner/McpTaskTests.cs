using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Commands.Agentics.Runner;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using PKS.Infrastructure.Services.Runner.McpTask;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>mcp_task job type (customers/famai/docs/mcp-task-protocol.md). No network, no processes.</summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class McpTaskTests
{
    private const string SampleConfig = """
    {
      // comments allowed
      "servers": {
        "aula": {
          "command": "/usr/bin/bun",
          "args": ["server.ts"],
          "env": { "AULA_MCP_DIR": "/x" },
          "denyTools": ["aula.contacts.list", "aula.raw_request"]
        },
        "handle": {
          "command": "uvx", "args": ["handle-mcp@latest"],
          "allowTools": ["search_products", "get_order"],
          "denyTools": ["search_products"]
        }
      }
    }
    """;

    // ---------- config ----------

    [Fact]
    public void Parse_ReadsServersArgsEnvAndLists()
    {
        var cfg = McpTaskConfig.Parse(SampleConfig, out var error);
        error.Should().BeNull();
        cfg!.Servers.Keys.Should().BeEquivalentTo("aula", "handle");
        cfg.Servers["aula"].Command.Should().Be("/usr/bin/bun");
        cfg.Servers["aula"].Args.Should().Equal("server.ts");
        cfg.Servers["aula"].Env!["AULA_MCP_DIR"].Should().Be("/x");
        cfg.Capabilities().Should().Equal("mcp-task:v1", "mcp-server:aula", "mcp-server:handle");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"servers":{"a":{"args":[]}}}""")]
    [InlineData("""{"servers":{"bad name":{"command":"x"}}}""")]
    public void Parse_RejectsMalformed(string json)
    {
        McpTaskConfig.Parse(json, out var error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryLoad_MissingFile_IsNullWithoutError()
    {
        McpTaskConfig.TryLoad(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json"), out var error).Should().BeNull();
        error.Should().BeNull();
    }

    [Fact]
    public void ResolvePath_PrefersCliThenEnvThenDefault()
    {
        McpTaskConfig.ResolvePath("/cli.json", _ => "/env.json", "/def.json").Should().Be("/cli.json");
        McpTaskConfig.ResolvePath(null, k => k == McpTaskConfig.EnvVar ? "/env.json" : null, "/def.json").Should().Be("/env.json");
        McpTaskConfig.ResolvePath(null, _ => null, "/def.json").Should().Be("/def.json");
    }

    [Fact]
    public void AllowList_WinsOverDenyList_DenyListOtherwise()
    {
        var cfg = McpTaskConfig.Parse(SampleConfig, out _)!;
        // handle: allowTools present => only those, denyTools ignored.
        cfg.Servers["handle"].IsToolAllowed("search_products").Should().BeTrue();
        cfg.Servers["handle"].IsToolAllowed("get_order").Should().BeTrue();
        cfg.Servers["handle"].IsToolAllowed("list_offers").Should().BeFalse();
        // aula: denylist.
        cfg.Servers["aula"].IsToolAllowed("aula.contacts.list").Should().BeFalse();
        cfg.Servers["aula"].IsToolAllowed("aula.discover").Should().BeTrue();
        new McpTaskServerConfig { Command = "x" }.IsToolAllowed("anything").Should().BeTrue();
    }

    // ---------- tool names ----------

    [Fact]
    public void NameMap_ReplacesDotsAndResolvesBack()
    {
        var map = new McpToolNameMap();
        var exposed = map.Add("aula", "aula.messages.list");
        exposed.Should().Be("aula__aula_messages_list");
        map.TryResolve(exposed, out var server, out var tool).Should().BeTrue();
        server.Should().Be("aula");
        tool.Should().Be("aula.messages.list");
        map.TryResolve("aula__nope", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void NameMap_DeduplicatesCollisionsAndStaysWithinApiLimits()
    {
        var map = new McpToolNameMap();
        var a = map.Add("s", "a.b");
        var b = map.Add("s", "a_b");
        a.Should().NotBe(b);
        map.TryResolve(b, out _, out var tool).Should().BeTrue();
        tool.Should().Be("a_b");

        var longName = map.Add("server", new string('x', 100) + " weird/chars");
        longName.Length.Should().BeLessThanOrEqualTo(64);
        longName.Should().MatchRegex("^[a-zA-Z0-9_-]{1,64}$");
    }

    // ---------- validation ----------

    [Fact]
    public async Task UnknownServer_FailsWithoutSpawningOrCallingModel()
    {
        var launcher = new FakeLauncher();
        var handler = new ScriptedHandler();
        var exec = new McpTaskExecutor(launcher, new HttpClient(handler), Env("sk-test"));
        var cfg = McpTaskConfig.Parse(SampleConfig, out _);

        var outcome = await exec.RunAsync(new McpTaskRequest { Prompt = "hej", McpServers = ["aula", "evil"] }, cfg, null, default);

        outcome.Success.Should().BeFalse();
        outcome.CompletionReason.Should().Be("unknown_mcp_server");
        outcome.Logs.Should().Contain("evil");
        launcher.Started.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingApiKey_FailsCleanlyAsAuth()
    {
        var exec = new McpTaskExecutor(new FakeLauncher(), new HttpClient(new ScriptedHandler()), Env(null));
        var outcome = await exec.RunAsync(new McpTaskRequest { Prompt = "hej", McpServers = ["aula"] },
            McpTaskConfig.Parse(SampleConfig, out _), null, default);
        outcome.CompletionReason.Should().Be("anthropic_auth");
    }

    [Fact]
    public async Task Api401_FailsAsAuth_AndKillsServers()
    {
        var launcher = new FakeLauncher();
        var handler = new ScriptedHandler();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""),
        });
        var exec = new McpTaskExecutor(launcher, new HttpClient(handler), Env("bad"));

        var outcome = await exec.RunAsync(new McpTaskRequest { Prompt = "hej", McpServers = ["aula"] },
            McpTaskConfig.Parse(SampleConfig, out _), null, default);

        outcome.Success.Should().BeFalse();
        outcome.CompletionReason.Should().Be("anthropic_auth");
        launcher.Started.Should().ContainSingle().Which.Disposed.Should().BeTrue();
    }

    // ---------- tool loop ----------

    [Fact]
    public async Task ToolLoop_ExecutesToolUse_FiltersTools_AndReportsNamesOnly()
    {
        var launcher = new FakeLauncher();
        var handler = new ScriptedHandler();
        handler.Enqueue(new JsonObject
        {
            ["stop_reason"] = "tool_use",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "Jeg kigger." },
                new JsonObject { ["type"] = "tool_use", ["id"] = "tu_1", ["name"] = "aula__aula_messages_list", ["input"] = new JsonObject { ["limit"] = 5 } },
                new JsonObject { ["type"] = "tool_use", ["id"] = "tu_2", ["name"] = "aula__aula_contacts_list", ["input"] = new JsonObject() },
            },
            ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 20 },
        });
        handler.Enqueue(new JsonObject
        {
            ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Der er forældremøde torsdag." } },
            ["usage"] = new JsonObject { ["input_tokens"] = 300, ["output_tokens"] = 30 },
        });
        var exec = new McpTaskExecutor(launcher, new HttpClient(handler), Env("sk-test", "https://gw.example/v1"));
        var stages = new List<string>();

        var outcome = await exec.RunAsync(new McpTaskRequest { Prompt = "Hvad sker der?", McpServers = ["aula"] },
            McpTaskConfig.Parse(SampleConfig, out _), stages.Add, default);

        outcome.Success.Should().BeTrue(outcome.Logs);
        var r = outcome.Result!;
        r.Text.Should().Be("Der er forældremøde torsdag.");
        r.Model.Should().Be("claude-haiku-5-5");
        r.Steps.Should().Be(2);
        r.Usage.InputTokens.Should().Be(400);
        r.Usage.OutputTokens.Should().Be(50);
        // The denied tool was never exposed, so the model's call to it resolves to an error result -- not a server call.
        r.ToolCalls.Should().ContainSingle();
        r.ToolCalls[0].Server.Should().Be("aula");
        r.ToolCalls[0].Tool.Should().Be("aula.messages.list");
        r.ToolCalls[0].Ok.Should().BeTrue();
        launcher.Started.Single().Calls.Should().Equal("aula.messages.list");
        launcher.Started.Single().LastArgs!["limit"].Should().BeOfType<JsonElement>().Which.GetInt32().Should().Be(5);
        launcher.Started.Single().Disposed.Should().BeTrue();

        // Request shape: base URL /v1 normalised, headers, tools filtered, defaults applied.
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Url.Should().Be("https://gw.example/v1/messages");
        handler.Requests[0].ApiKey.Should().Be("sk-test");
        handler.Requests[0].Version.Should().Be("2023-06-01");
        var first = JsonNode.Parse(handler.Requests[0].Body)!.AsObject();
        first["model"]!.GetValue<string>().Should().Be("claude-haiku-5-5");
        first["max_tokens"]!.GetValue<int>().Should().Be(2000);
        first["system"]!.GetValue<string>().Should().Be(McpTaskExecutor.DefaultSystemPrompt);
        first["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())
            .Should().BeEquivalentTo("aula__aula_messages_list", "aula__aula_discover");

        // Second request: both tool_results in ONE user message, the unknown one flagged is_error.
        var second = JsonNode.Parse(handler.Requests[1].Body)!.AsObject();
        var msgs = second["messages"]!.AsArray();
        msgs.Should().HaveCount(3);
        var results = msgs[2]!["content"]!.AsArray();
        results.Should().HaveCount(2);
        results[0]!["tool_use_id"]!.GetValue<string>().Should().Be("tu_1");
        results[0]!["content"]!.GetValue<string>().Should().Be("result of aula.messages.list");
        results[1]!["is_error"]!.GetValue<bool>().Should().BeTrue();

        stages.Should().Contain("step 1/12").And.Contain("tool aula/aula.messages.list");
    }

    [Fact]
    public async Task ToolResults_AreTruncatedTo40000Chars()
    {
        var launcher = new FakeLauncher { ResultText = new string('a', 50_000) };
        var handler = new ScriptedHandler();
        handler.Enqueue(ToolUse("aula__aula_discover"));
        handler.Enqueue(EndTurn("ok"));
        var exec = new McpTaskExecutor(launcher, new HttpClient(handler), Env("k"));

        await exec.RunAsync(new McpTaskRequest { Prompt = "p", McpServers = ["aula"] }, McpTaskConfig.Parse(SampleConfig, out _), null, default);

        var sent = JsonNode.Parse(handler.Requests[1].Body)!["messages"]![2]!["content"]![0]!["content"]!.GetValue<string>();
        sent.Length.Should().BeLessThan(40_100);
        sent.Should().StartWith(new string('a', 40_000)).And.EndWith("[truncated]");
    }

    [Fact]
    public async Task LastStep_ForcesAnswer_AndExhaustionFails()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(ToolUse("aula__aula_discover"));
        handler.Enqueue(ToolUse("aula__aula_discover"));
        var exec = new McpTaskExecutor(new FakeLauncher(), new HttpClient(handler), Env("k"));

        var outcome = await exec.RunAsync(new McpTaskRequest { Prompt = "p", McpServers = ["aula"], MaxSteps = 2 },
            McpTaskConfig.Parse(SampleConfig, out _), null, default);

        JsonNode.Parse(handler.Requests[0].Body)!["tool_choice"].Should().BeNull();
        JsonNode.Parse(handler.Requests[1].Body)!["tool_choice"]!["type"]!.GetValue<string>().Should().Be("none");
        outcome.Success.Should().BeFalse();
        outcome.CompletionReason.Should().Be("max_steps_exceeded");
    }

    // ---------- PATCH payload ----------

    [Fact]
    public void CompletionPatch_OmitsNulls_AndCarriesResultOnlyOnSuccess()
    {
        var ok = RunnerJobCompletionPatch.From(new McpTaskOutcome
        {
            Success = true,
            Result = new McpTaskResult { Text = "t", Model = "m", Steps = 1, ToolCalls = [new() { Server = "aula", Tool = "aula.discover", Ok = true, Ms = 812 }] },
        }).ToJson();
        var okNode = JsonNode.Parse(ok)!.AsObject();
        okNode["status"]!.GetValue<string>().Should().Be("completed");
        okNode["conclusion"]!.GetValue<string>().Should().Be("success");
        okNode.ContainsKey("completionReason").Should().BeFalse();
        okNode.ContainsKey("logs").Should().BeFalse();
        okNode["result"]!["toolCalls"]![0]!["ms"]!.GetValue<long>().Should().Be(812);
        okNode["result"]!["usage"]!["inputTokens"]!.GetValue<long>().Should().Be(0);

        var fail = JsonNode.Parse(RunnerJobCompletionPatch.From(McpTaskOutcome.Fail("unknown_mcp_server", "x")).ToJson())!.AsObject();
        fail["conclusion"]!.GetValue<string>().Should().Be("failure");
        fail["completionReason"]!.GetValue<string>().Should().Be("unknown_mcp_server");
        fail["logs"]!.GetValue<string>().Should().Be("x");
        fail.ContainsKey("result").Should().BeFalse();
    }

    // ---------- capabilities ----------

    [Fact]
    public async Task Capabilities_AdvertiseMcpOnlyWhenConfigExistsAndParses()
    {
        var command = BuildCommand();
        var dir = Path.Combine(Path.GetTempPath(), $"mcp-task-caps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            command.McpTaskDefaultConfigPathOverride = Path.Combine(dir, "absent.json");
            var without = await command.ComputeCapabilitiesAsync(true, "http://localhost:1/v1", "gpt-5.5", default);
            without.Should().NotContain(c => c.StartsWith("mcp-"));

            var good = Path.Combine(dir, "good.json");
            await File.WriteAllTextAsync(good, SampleConfig);
            var with = await command.ComputeCapabilitiesAsync(true, "http://localhost:1/v1", "gpt-5.5", default,
                capabilityOverride: ["alp_operator"], mcpConfigPath: good);
            with.Should().Contain(["mcp-task:v1", "mcp-server:aula", "mcp-server:handle"]);
            with.Should().NotContain("chat-llm:v1"); // the override still narrows everything else

            var bad = Path.Combine(dir, "bad.json");
            await File.WriteAllTextAsync(bad, "{ nope");
            var broken = await command.ComputeCapabilitiesAsync(true, "http://localhost:1/v1", "gpt-5.5", default, mcpConfigPath: bad);
            broken.Should().NotContain(c => c.StartsWith("mcp-"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- dispatch wiring ----------

    [Theory]
    [InlineData(null, "mcp_config_missing")]
    [InlineData("""{"servers":{"handle":{"command":"uvx"}}}""", "unknown_mcp_server")]
    public async Task PollAndDispatch_ClaimsMcpTaskFromNeedsAlone_AndPatchesFailure(string? configJson, string expectedReason)
    {
        var registration = new AgenticsRunnerRegistration
        {
            Id = "runner-1",
            Name = "t",
            Token = "tok",
            Owner = "fam",
            Project = "home",
            Server = "https://famai.test",
            RegisteredAt = DateTime.UtcNow,
        };
        var baseUrl = $"{registration.Server}/api/owners/fam/projects/home";
        var patches = new List<string>();
        var claimed = new List<string>();
        var handler = new LambdaHandler(async req =>
        {
            var url = req.RequestUri!.ToString();
            var body = req.Content != null ? await req.Content.ReadAsStringAsync() : "";
            string json = "{}";
            if (url == $"{baseUrl}/runners/jobs")
                // The poll deliberately omits agentDefinition: only `needs` says what this job is.
                json = """{"jobs":[{"id":"j1","needs":["mcp-task:v1","mcp-server:aula"]}]}""";
            else if (url == $"{baseUrl}/runners/generate-jitconfig")
            {
                claimed.Add(body);
                json = """{"job":{"runId":"r1","agentDefinition":{"jobType":"mcp_task","prompt":"hej","mcpServers":["aula"]}}}""";
            }
            else if (req.Method == HttpMethod.Patch && url == $"{baseUrl}/runs/r1/jobs/j1")
                lock (patches) patches.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var command = BuildCommand(factory.Object);

        var dir = Path.Combine(Path.GetTempPath(), $"mcp-task-dispatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            command.McpTaskDefaultConfigPathOverride = Path.Combine(dir, "mcp-task-servers.json");
            if (configJson != null) await File.WriteAllTextAsync(command.McpTaskDefaultConfigPathOverride, configJson);

            var processed = await command.PollAndDispatchOnceAsync(registration,
                new AgenticsRunnerRunCommand.Settings { InProcess = false, PollingInterval = 10 },
                credentialServer: null, chatLlmBackendUrl: "http://localhost:1/v1", chatLlmBackendKey: null,
                chatLlmModelId: "gpt-5.5", chatLlmVerbose: false, ct: CancellationToken.None);
            processed.Should().Be(1);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && patches.Count < 2) await Task.Delay(50);

            claimed.Should().ContainSingle().Which.Should().Contain("j1");
            patches.Should().HaveCount(2);
            patches[0].Should().Contain("in_progress");
            var final = JsonNode.Parse(patches[1])!.AsObject();
            final["status"]!.GetValue<string>().Should().Be("completed");
            final["conclusion"]!.GetValue<string>().Should().Be("failure");
            final["completionReason"]!.GetValue<string>().Should().Be(expectedReason);
            final.ContainsKey("result").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- helpers ----------

    private static Func<string, string?> Env(string? apiKey, string? baseUrl = null) => k => k switch
    {
        "ANTHROPIC_API_KEY" => apiKey,
        "ANTHROPIC_BASE_URL" => baseUrl,
        _ => null,
    };

    private static JsonObject ToolUse(string name) => new()
    {
        ["stop_reason"] = "tool_use",
        ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = $"tu_{Guid.NewGuid():N}", ["name"] = name, ["input"] = new JsonObject() } },
    };

    private static JsonObject EndTurn(string text) => new()
    {
        ["stop_reason"] = "end_turn",
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
    };

    private static AgenticsRunnerRunCommand BuildCommand(IHttpClientFactory? httpClientFactory = null)
    {
        var foundryAuthService = new Mock<IAzureFoundryAuthService>();
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Capabilities.Interactive = false;
        return new AgenticsRunnerRunCommand(
            new Mock<IAgenticsRunnerConfigurationService>().Object,
            new Mock<IDevcontainerSpawnerService>().Object,
            httpClientFactory ?? new Mock<IHttpClientFactory>().Object,
            new Mock<IGitHubAuthenticationService>().Object,
            foundryAuthService.Object,
            new AzureFoundryAuthConfig(),
            new AgentChatProviderFactory(new Mock<IConfigurationService>().Object, new HttpClient(), FakeSecretResolver.Empty, foundryAuthService.Object),
            console,
            new Mock<IRunnerExecutionCapabilityProbe>().Object);
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => f(request);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public readonly Queue<HttpResponseMessage> Responses = new();
        public readonly List<(string Url, string Body, string? ApiKey, string? Version)> Requests = new();

        public void Enqueue(JsonObject body) =>
            Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct),
                request.Headers.TryGetValues("x-api-key", out var k) ? k.Single() : null,
                request.Headers.TryGetValues("anthropic-version", out var v) ? v.Single() : null));
            return Responses.Count > 0 ? Responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private sealed class FakeLauncher : IMcpServerLauncher
    {
        public readonly List<FakeSession> Started = new();
        public string? ResultText { get; init; }

        public Task<IMcpServerSession> StartAsync(string serverName, McpTaskServerConfig config, CancellationToken ct)
        {
            var s = new FakeSession(serverName, ResultText);
            Started.Add(s);
            return Task.FromResult<IMcpServerSession>(s);
        }
    }

    private sealed class FakeSession(string name, string? resultText) : IMcpServerSession
    {
        public string ServerName => name;
        public bool Disposed { get; private set; }
        public readonly List<string> Calls = new();
        public IReadOnlyDictionary<string, object?>? LastArgs { get; private set; }

        public Task<IReadOnlyList<McpServerTool>> ListToolsAsync(CancellationToken ct)
        {
            var schema = JsonDocument.Parse("""{"type":"object","properties":{"limit":{"type":"integer"}}}""").RootElement;
            IReadOnlyList<McpServerTool> tools =
            [
                new("aula.messages.list", "List messages", schema),
                new("aula.contacts.list", "Contacts", schema),
                new("aula.discover", null, default),
            ];
            return Task.FromResult(tools);
        }

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
        {
            Calls.Add(toolName);
            LastArgs = args;
            return Task.FromResult((resultText ?? $"result of {toolName}", false));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Opt-in smoke test: real stdio spawn of @modelcontextprotocol/server-everything via npx and a
/// bogus API key, so the run must end in a clean anthropic_auth failure. Set MCP_TASK_SMOKE=1 to run.</summary>
[Trait("Category", "Integration")]
[Trait("Speed", "Slow")]
public class McpTaskSmokeTests
{
    [Fact]
    public async Task ServerEverything_ListsTools_ThenFailsCleanlyOnAuth_AndLeavesNoProcess()
    {
        if (Environment.GetEnvironmentVariable("MCP_TASK_SMOKE") != "1") return;

        var cfg = McpTaskConfig.Parse("""
        { "servers": { "everything": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-everything"], "denyTools": ["longRunningOperation"] } } }
        """, out var err)!;
        err.Should().BeNull();
        var log = new List<string>();
        var exec = new McpTaskExecutor(new StdioMcpServerLauncher(), new HttpClient(),
            k => k == "ANTHROPIC_API_KEY" ? "sk-ant-invalid-smoke" : null, log.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var outcome = await exec.RunAsync(new McpTaskRequest { Prompt = "echo hej", McpServers = ["everything"] }, cfg, null, cts.Token);

        outcome.Success.Should().BeFalse();
        outcome.CompletionReason.Should().Be("anthropic_auth", outcome.Logs);
        log.Should().Contain(l => l.StartsWith("MCP task: ") && !l.StartsWith("MCP task: 0 tool"));

        await Task.Delay(1000);
        var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pgrep", "-af server-everything") { RedirectStandardOutput = true })!;
        // Only node/npm processes count -- the shell that launched `dotnet test` may mention the name too.
        var survivors = string.Join("\n", (await ps.StandardOutput.ReadToEndAsync()).Split('\n')
            .Where(l => Path.GetFileName(l.Split(' ', 3).ElementAtOrDefault(1) ?? "") is "node" or "npm" or "npx" or "sh" && !l.Contains("zsh")));
        await ps.WaitForExitAsync();
        survivors.Should().BeEmpty("all spawned MCP servers must be killed when the job ends");
    }

    [Fact]
    public async Task ServerEverything_ChildDoesNotSeeRunnerSecrets_AndCancelKillsIt()
    {
        if (Environment.GetEnvironmentVariable("MCP_TASK_SMOKE") != "1") return;
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-should-not-leak");
        try
        {
            var server = new McpTaskServerConfig { Command = "npx", Args = ["-y", "@modelcontextprotocol/server-everything"], Env = new() { ["FAMAI_VISIBLE"] = "yes" } };
            await using (var session = await new StdioMcpServerLauncher().StartAsync("everything", server, CancellationToken.None))
            {
                var tools = await session.ListToolsAsync(CancellationToken.None);
                tools.Should().NotBeEmpty();
                var envTool = tools.Select(t => t.Name).FirstOrDefault(n => n.Contains("env", StringComparison.OrdinalIgnoreCase));
                envTool.Should().NotBeNull("server-everything exposes an env-printing tool");
                var (envText, _) = await session.CallToolAsync(envTool!, new Dictionary<string, object?>(), CancellationToken.None);
                envText.Should().Contain("FAMAI_VISIBLE");
                envText.Should().NotContain("sk-ant-should-not-leak");

                var longTool = tools.Select(t => t.Name).FirstOrDefault(n => n.Contains("long", StringComparison.OrdinalIgnoreCase));
                if (longTool != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    var act = () => session.CallToolAsync(longTool, new Dictionary<string, object?> { ["duration"] = 30, ["steps"] = 5 }, cts.Token);
                    await act.Should().ThrowAsync<OperationCanceledException>();
                }
            }

            await Task.Delay(1000);
            McpTaskSmokeTests.Survivors().Should().BeEmpty("disposing the session must kill the server even after a cancelled call");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    [Fact]
    public async Task NonMcpCommand_FailsToStart_AndLeavesNoOrphan()
    {
        if (Environment.GetEnvironmentVariable("MCP_TASK_SMOKE") != "1") return;
        var marker = $"mcp-smoke-{Guid.NewGuid():N}";
        // A unique duration so the grandchild `sleep` is findable too (its own cmdline has no marker).
        var sleepArg = $"61.{Random.Shared.Next(100000, 999999)}";
        var cfg = new McpTaskConfig { Servers = new() { ["dud"] = new McpTaskServerConfig { Command = "sh", Args = ["-c", $"sleep {sleepArg}; echo {marker}"] } } };
        var exec = new McpTaskExecutor(new StdioMcpServerLauncher(), new HttpClient(), k => k == "ANTHROPIC_API_KEY" ? "x" : null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Func<Task> act = () => exec.RunAsync(new McpTaskRequest { Prompt = "p", McpServers = ["dud"] }, cfg, null, cts.Token);
        // Either a clean start failure or the cancellation -- but never an orphaned `sleep`.
        try { await act(); } catch (OperationCanceledException) { }

        await Task.Delay(1500);
        var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pgrep", $"-f {marker}|sleep {sleepArg}") { RedirectStandardOutput = true })!;
        var pids = (await ps.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Trim() != Environment.ProcessId.ToString()).ToList();
        await ps.WaitForExitAsync();
        pids.Should().BeEmpty();
    }

    internal static string Survivors()
    {
        var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pgrep", "-af server-everything") { RedirectStandardOutput = true })!;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return string.Join("\n", output.Split('\n')
            .Where(l => Path.GetFileName(l.Split(' ', 3).ElementAtOrDefault(1) ?? "") is "node" or "npm" or "npx" or "sh" && !l.Contains("zsh")));
    }
}
