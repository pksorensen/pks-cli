using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Services.Azure;

public class AzurePimRetryTests
{
    private const string SubId = "11111111-2222-3333-4444-555555555555";
    private const string Sub = "/subscriptions/" + SubId;
    private const string Owner = "/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static readonly JsonNode OneEligibleOwner = new JsonObject
    {
        ["value"] = new JsonArray(new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["roleEligibilityScheduleId"] = $"{Sub}/providers/Microsoft.Authorization/roleEligibilitySchedules/owner",
                ["principalId"] = "user-1",
                ["roleDefinitionId"] = Sub + Owner,
                ["scope"] = Sub,
                ["expandedProperties"] = new JsonObject
                {
                    ["roleDefinition"] = new JsonObject { ["displayName"] = "Owner" },
                    ["scope"] = new JsonObject { ["displayName"] = "Sponsorship", ["type"] = "subscription" },
                },
            },
        }),
    };

    private static HttpRequestException Forbidden() => new("ARM 403 starting VM", null, HttpStatusCode.Forbidden);

    [Fact]
    public async Task A_403_activates_the_chosen_role_and_retries_until_it_takes_effect()
    {
        var handler = new Handler(req => req.Method == HttpMethod.Put
            ? Json(new JsonObject { ["properties"] = new JsonObject { ["status"] = "Provisioned" } }, HttpStatusCode.Created)
            : req.RequestUri!.AbsolutePath.EndsWith("roleEligibilityScheduleInstances")
                ? Json(OneEligibleOwner)
                : Json(new JsonObject { ["value"] = new JsonArray() }));
        var console = new TestConsole().Interactive();
        console.Input.PushKey(ConsoleKey.Spacebar); // select Owner
        console.Input.PushKey(ConsoleKey.Enter);    // confirm
        console.Input.PushKey(ConsoleKey.Enter);    // justification: default
        console.Input.PushKey(ConsoleKey.Enter);    // hours: default
        var attempts = 0;

        await AzurePimRetry.RunAsync(
            () => ++attempts < 3 ? throw Forbidden() : Task.CompletedTask,
            console, new HttpClient(handler), "tok", SubId, "start VM test", TimeSpan.Zero);

        attempts.Should().Be(3, "the first call is refused, the second races the activation, the third lands");
        handler.Calls.Should().Contain(c => c.StartsWith("PUT ") && c.Contains("roleAssignmentScheduleRequests"));
        console.Output.Should().Contain("Owner");
    }

    [Fact]
    public async Task A_403_with_nothing_to_activate_is_the_original_error()
    {
        var handler = new Handler(_ => Json(new JsonObject { ["value"] = new JsonArray() }));

        var act = () => AzurePimRetry.RunAsync(
            () => throw Forbidden(), new TestConsole(), new HttpClient(handler), "tok", SubId, "start VM test", TimeSpan.Zero);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Other_failures_pass_straight_through_without_asking_PIM()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("PIM must not be asked"));

        var act = () => AzurePimRetry.RunAsync(
            () => throw new HttpRequestException("ARM 409", null, HttpStatusCode.Conflict),
            new TestConsole(), new HttpClient(handler), "tok", SubId, "start VM test", TimeSpan.Zero);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Conflict);
        handler.Calls.Should().BeEmpty();
    }
}
