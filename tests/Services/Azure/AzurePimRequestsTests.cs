using System.Text.Json.Nodes;
using FluentAssertions;
using PKS.Infrastructure.Services.Azure;
using Xunit;

namespace PKS.CLI.Tests.Services.Azure;

public class AzurePimRequestsTests
{
    private const string Sub = "/subscriptions/11111111-2222-3333-4444-555555555555";
    private const string Owner = "/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    private const string Contributor = "/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c";
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static JsonNode Eligible(params (string role, string scope, string name)[] items) => new JsonObject
    {
        ["value"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["roleEligibilityScheduleId"] = $"{i.scope}/providers/Microsoft.Authorization/roleEligibilitySchedules/{i.name}",
                ["principalId"] = "user-1",
                // ARM returns the definition id prefixed with the subscription at subscription scope.
                ["roleDefinitionId"] = Sub + i.role,
                ["scope"] = i.scope,
                ["expandedProperties"] = new JsonObject
                {
                    ["roleDefinition"] = new JsonObject { ["displayName"] = i.name },
                    ["scope"] = new JsonObject { ["displayName"] = "Sponsorship", ["type"] = "subscription" },
                },
            },
        }).ToArray()),
    };

    private static JsonNode Active(string role, string scope, string type, DateTimeOffset? end) => new JsonObject
    {
        ["value"] = new JsonArray(new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["assignmentType"] = type,
                ["roleDefinitionId"] = role,
                ["scope"] = scope,
                ["endDateTime"] = end?.ToString("o"),
            },
        }),
    };

    [Fact]
    public void Eligibility_without_activation_is_inactive()
    {
        var roles = AzurePimRequests.Parse(Eligible((Owner, Sub, "Owner")), null, Now);

        roles.Should().ContainSingle();
        roles[0].RoleName.Should().Be("Owner");
        roles[0].IsActive.Should().BeFalse();
        roles[0].PrincipalId.Should().Be("user-1");
    }

    [Fact]
    public void Activation_matches_on_role_guid_and_scope_regardless_of_id_prefix()
    {
        var roles = AzurePimRequests.Parse(
            Eligible((Owner, Sub, "Owner"), (Contributor, Sub, "Contributor")),
            Active(Owner, Sub + "/", "Activated", Now.AddHours(3)), Now);

        roles.Single(r => r.RoleName == "Owner").IsActive.Should().BeTrue();
        roles.Single(r => r.RoleName == "Owner").ActiveUntil.Should().Be(Now.AddHours(3));
        roles.Single(r => r.RoleName == "Contributor").IsActive.Should().BeFalse();
    }

    [Fact]
    public void Permanent_assignment_and_expired_activation_do_not_count_as_activated()
    {
        AzurePimRequests.Parse(Eligible((Owner, Sub, "Owner")), Active(Owner, Sub, "Assigned", null), Now)
            .Single().IsActive.Should().BeFalse();
        AzurePimRequests.Parse(Eligible((Owner, Sub, "Owner")), Active(Owner, Sub, "Activated", Now.AddMinutes(-1)), Now)
            .Single().IsActive.Should().BeFalse();
    }

    [Fact]
    public void Duplicate_instances_collapse_to_one()
    {
        AzurePimRequests.Parse(Eligible((Owner, Sub, "Owner"), (Owner, Sub, "Owner")), null, Now)
            .Should().ContainSingle();
    }
}
