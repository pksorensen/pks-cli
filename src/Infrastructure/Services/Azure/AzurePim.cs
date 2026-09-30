using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>An Azure RBAC role the signed-in user is PIM-eligible for.</summary>
public sealed record AzurePimEligibility(
    string EligibilityScheduleId,
    string PrincipalId,
    string RoleDefinitionId,
    string RoleName,
    string Scope,
    string ScopeName,
    string ScopeType,
    bool IsActive,
    DateTimeOffset? ActiveUntil);

/// <summary>The outcome of one self-activation request.</summary>
public sealed record AzurePimActivationResult(bool Success, string Status, string? Error);

/// <summary>
/// Azure resource PIM (Privileged Identity Management) over ARM. A PIM-eligible user has no
/// access until they activate the role, and ARM does not say so: listing resource groups answers
/// 200 with an empty list. So a command that is about to call resource APIs first asks which
/// roles the user could activate. Everything runs with the user's own ARM token; no Graph scope
/// is needed.
/// </summary>
public static class AzurePimRequests
{
    private const string ApiVersion = "2020-10-01";
    private const string Arm = "https://management.azure.com";

    /// <summary>Every resource-role eligibility of the signed-in user in the tenant, each marked
    /// active when an activation of it is currently in force. Tenant-wide first (the portal's
    /// "My roles" view); falls back to one subscription when the root listing is refused.</summary>
    public static async Task<List<AzurePimEligibility>> ListEligibilitiesAsync(
        HttpClient http, string token, string? subscriptionId, CancellationToken ct = default)
    {
        var scopes = new List<string> { "" };
        if (!string.IsNullOrEmpty(subscriptionId)) scopes.Add($"/subscriptions/{subscriptionId}");

        foreach (var scope in scopes)
        {
            var eligible = await GetAsync(http, token,
                $"{Arm}{scope}/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?api-version={ApiVersion}&$filter=asTarget()", ct);
            if (eligible == null) continue;
            var active = await GetAsync(http, token,
                $"{Arm}{scope}/providers/Microsoft.Authorization/roleAssignmentScheduleInstances?api-version={ApiVersion}&$filter=asTarget()", ct);
            return Parse(eligible, active, DateTimeOffset.UtcNow);
        }
        return new List<AzurePimEligibility>();
    }

    /// <summary>Pairs eligibility instances with the activated assignment instances that stem
    /// from them. Split out from the HTTP calls so it can be tested on recorded answers.</summary>
    public static List<AzurePimEligibility> Parse(JsonNode eligible, JsonNode? active, DateTimeOffset now)
    {
        var activeUntil = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in active?["value"]?.AsArray() ?? new JsonArray())
        {
            var p = item?["properties"];
            if (p == null || !string.Equals((string?)p["assignmentType"], "Activated", StringComparison.OrdinalIgnoreCase))
                continue;
            DateTimeOffset? end = DateTimeOffset.TryParse((string?)p["endDateTime"], out var e) ? e : null;
            if (end is { } until && until <= now) continue;
            var key = Key((string?)p["roleDefinitionId"], (string?)p["scope"]);
            activeUntil[key] = end;
        }

        var result = new List<AzurePimEligibility>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in eligible["value"]?.AsArray() ?? new JsonArray())
        {
            var p = item?["properties"];
            if (p == null) continue;
            var roleDefinitionId = (string?)p["roleDefinitionId"] ?? "";
            var scope = (string?)p["scope"] ?? "";
            var key = Key(roleDefinitionId, scope);
            if (roleDefinitionId == "" || !seen.Add(key)) continue;

            var expanded = p["expandedProperties"];
            var isActive = activeUntil.TryGetValue(key, out var until);
            result.Add(new AzurePimEligibility(
                EligibilityScheduleId: (string?)p["roleEligibilityScheduleId"] ?? "",
                PrincipalId: (string?)p["principalId"] ?? "",
                RoleDefinitionId: roleDefinitionId,
                RoleName: (string?)expanded?["roleDefinition"]?["displayName"] ?? roleDefinitionId.Split('/').Last(),
                Scope: scope,
                ScopeName: (string?)expanded?["scope"]?["displayName"] ?? scope,
                ScopeType: (string?)expanded?["scope"]?["type"] ?? "",
                IsActive: isActive,
                ActiveUntil: isActive ? until : null));
        }
        return result;
    }

    /// <summary>Self-activates one eligibility for <paramref name="duration"/>. A policy that
    /// needs approval answers PendingApproval, which counts as success but grants nothing yet.</summary>
    public static async Task<AzurePimActivationResult> ActivateAsync(
        HttpClient http, string token, AzurePimEligibility role, string justification, TimeSpan duration, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["principalId"] = role.PrincipalId,
                ["roleDefinitionId"] = role.RoleDefinitionId,
                ["requestType"] = "SelfActivate",
                ["linkedRoleEligibilityScheduleId"] = role.EligibilityScheduleId,
                ["justification"] = justification,
                ["scheduleInfo"] = new JsonObject
                {
                    ["startDateTime"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["expiration"] = new JsonObject
                    {
                        ["type"] = "AfterDuration",
                        ["duration"] = System.Xml.XmlConvert.ToString(duration),
                    },
                },
            },
        };
        var url = $"{Arm}{role.Scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{Guid.NewGuid()}?api-version={ApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(content); } catch { /* non-JSON error page */ }

        if (!response.IsSuccessStatusCode)
        {
            var message = (string?)json?["error"]?["message"] ?? content;
            var code = (string?)json?["error"]?["code"];
            return new AzurePimActivationResult(false, code ?? ((int)response.StatusCode).ToString(), message);
        }
        return new AzurePimActivationResult(true, (string?)json?["properties"]?["status"] ?? "Accepted", null);
    }

    private static string Key(string? roleDefinitionId, string? scope)
        => $"{roleDefinitionId?.Split('/').Last()}|{scope?.TrimEnd('/')}";

    private static async Task<JsonNode?> GetAsync(HttpClient http, string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>
/// The interactive half: shows the user's eligible roles and offers to activate the inactive
/// ones before a command calls resource APIs. Silent when there is nothing to activate.
/// </summary>
public static class AzurePimPrompt
{
    /// <summary>Returns true when at least one role was activated (the caller may want to wait
    /// for RBAC to propagate before listing resources).</summary>
    public static async Task<bool> OfferActivationAsync(
        IAnsiConsole console, HttpClient http, string token, string? subscriptionId, string purpose, CancellationToken ct = default)
    {
        List<AzurePimEligibility> roles = new();
        try
        {
            await console.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Checking PIM-eligible roles...", async _ =>
                    roles = await AzurePimRequests.ListEligibilitiesAsync(http, token, subscriptionId, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            console.MarkupLine($"[grey]Could not list PIM-eligible roles ({Markup.Escape(ex.Message)}) — continuing.[/]");
            return false;
        }
        // Only roles that can grant access in the subscription being used: those inside it, and
        // those above it (management group, root). Another subscription's roles are noise here.
        if (!string.IsNullOrEmpty(subscriptionId))
            roles = roles.Where(r => !r.Scope.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                || r.Scope.StartsWith($"/subscriptions/{subscriptionId}", StringComparison.OrdinalIgnoreCase)).ToList();
        if (roles.Count == 0) return false;

        var ordered = roles.OrderBy(r => r.ScopeName).ThenBy(r => r.RoleName).ToList();

        var table = new Table().Border(TableBorder.Rounded).Title("[cyan]Your PIM-eligible Azure roles[/]");
        table.AddColumn("Role"); table.AddColumn("Scope"); table.AddColumn("Status");
        foreach (var r in ordered)
            table.AddRow(Markup.Escape(r.RoleName), Markup.Escape($"{r.ScopeName} ({r.ScopeType})"),
                r.IsActive ? $"[green]active{(r.ActiveUntil is { } u ? $" until {u.ToLocalTime():HH:mm}" : "")}[/]" : "[yellow]not active[/]");
        console.Write(table);

        var inactive = ordered.Where(r => !r.IsActive).ToList();
        if (inactive.Count == 0) return false;

        var chosen = console.Prompt(
            new MultiSelectionPrompt<AzurePimEligibility>()
                .Title("[cyan]Activate before continuing?[/] [grey](space to select, enter to confirm — none to skip)[/]")
                .NotRequired()
                .PageSize(12)
                .UseConverter(r => Markup.Escape($"{r.RoleName} — {r.ScopeName}"))
                .AddChoices(inactive));
        if (chosen.Count == 0) return false;

        var justification = console.Prompt(new TextPrompt<string>("[cyan]Justification:[/]").DefaultValue(purpose));
        var hours = console.Prompt(new TextPrompt<int>("[cyan]Duration (hours):[/]").DefaultValue(8)
            .Validate(h => h is >= 1 and <= 24 ? ValidationResult.Success() : ValidationResult.Error("1–24 hours")));

        var anyActivated = false;
        foreach (var role in chosen)
        {
            var result = await AzurePimRequests.ActivateAsync(http, token, role, justification, TimeSpan.FromHours(hours), ct);
            // Policies commonly cap activation below 8 hours; one shorter retry beats a failure.
            if (!result.Success && result.Error?.Contains("Expiration", StringComparison.OrdinalIgnoreCase) == true && hours > 1)
            {
                console.MarkupLine($"[grey]{Markup.Escape(role.RoleName)}: {hours} h exceeds the policy — retrying with 1 h.[/]");
                result = await AzurePimRequests.ActivateAsync(http, token, role, justification, TimeSpan.FromHours(1), ct);
            }

            var label = Markup.Escape($"{role.RoleName} — {role.ScopeName}");
            if (!result.Success)
            {
                console.MarkupLine($"[red]✗ {label}: {Markup.Escape(result.Error ?? result.Status)}[/]");
                if (result.Status.Contains("Acrs", StringComparison.OrdinalIgnoreCase) || result.Error?.Contains("MFA", StringComparison.OrdinalIgnoreCase) == true)
                    console.MarkupLine("[grey]  This role requires MFA at activation — activate it in the Azure portal (PIM → My roles).[/]");
            }
            else if (result.Status.Contains("Pending", StringComparison.OrdinalIgnoreCase))
            {
                console.MarkupLine($"[yellow]… {label}: waiting for approval[/]");
            }
            else
            {
                console.MarkupLine($"[green]✓ {label}: activated for {hours} h[/]");
                anyActivated = true;
            }
        }
        return anyActivated;
    }
}

/// <summary>
/// Runs an ARM operation; on a 403 offers PIM activation (an eligible role grants nothing until
/// activated, and ARM only says "AuthorizationFailed") and retries while the activation propagates.
/// The operation must be safe to run again after a partial 403.
/// </summary>
public static class AzurePimRetry
{
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> operation, IAnsiConsole console, HttpClient http,
        string token, string subscriptionId, string purpose, TimeSpan? propagationDelay = null)
    {
        try
        {
            return await operation();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            console.MarkupLine("[yellow]Azure refused this (403). If your role there is a PIM eligibility, it is not active yet.[/]");
            if (!await AzurePimPrompt.OfferActivationAsync(console, http, token, subscriptionId, purpose))
                throw;
        }

        // An activation takes a little while to reach every resource provider (observed: Compute
        // still refused ~30 s after Network accepted).
        for (var attempt = 1; ; attempt++)
        {
            console.MarkupLine("[dim]Waiting for the activation to take effect...[/]");
            await Task.Delay(propagationDelay ?? TimeSpan.FromSeconds(30));
            try
            {
                return await operation();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden && attempt < 4)
            {
            }
        }
    }

    public static Task RunAsync(
        Func<Task> operation, IAnsiConsole console, HttpClient http,
        string token, string subscriptionId, string purpose, TimeSpan? propagationDelay = null)
        => RunAsync(async () => { await operation(); return true; }, console, http, token, subscriptionId, purpose, propagationDelay);
}
