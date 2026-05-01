using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Templates;

/// <summary>
/// Resolves a curated devcontainer template (e.g. PKS.Templates.PksFullstack) into the
/// in-memory file map the runner writes into a freshly-cloned project before
/// `devcontainer up`. Used by the spawner when the job spec carries a template reference
/// and no explicit InlineDevcontainerFiles override.
/// </summary>
public interface IDevcontainerTemplateRendererService
{
    /// <summary>
    /// Renders the named template into a {relativePath -> fileContent} dictionary, applying
    /// placeholder substitution (e.g. {{ProjectName}}). Returns null if the template can't
    /// be resolved (unknown source, package not found, network failure) — caller should
    /// log a warning and fall through to its own fallback behavior.
    /// </summary>
    /// <param name="template">The template reference. Source defaults to "nuget" when null/empty.</param>
    /// <param name="placeholderValues">Variables for substitution (ProjectName, Description, ...).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Dictionary<string, string>?> RenderAsync(
        DevcontainerTemplateRef template,
        IReadOnlyDictionary<string, string> placeholderValues,
        CancellationToken ct = default);
}
