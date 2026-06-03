using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent.Anthropic;
using Spectre.Console;

namespace PKS.Commands.Claude;

/// <summary>
/// <c>pks claude scaleway</c> — pick any model from the full Scaleway catalog, then launch Claude
/// Code against it via the translating proxy.
/// </summary>
[Description("Run Claude Code on a Scaleway serverless model (full catalog picker)")]
public sealed class ClaudeScalewayCommand : ScalewayProxyCommandBase
{
    public ClaudeScalewayCommand(IScalewayService scaleway, IAnsiConsole console) : base(scaleway, console) { }
    protected override IReadOnlyList<GenerativeModel> Candidates() =>
        GenerativeModelCatalog.ByProvider(GenerativeModelCatalog.ScalewayProvider);
    protected override string FamilyLabel => "Scaleway";
}

/// <summary><c>pks claude mistral</c> — Mistral/Devstral versions on Scaleway.</summary>
[Description("Run Claude Code on a Mistral / Devstral model (Scaleway)")]
public sealed class ClaudeMistralCommand : ScalewayProxyCommandBase
{
    public ClaudeMistralCommand(IScalewayService scaleway, IAnsiConsole console) : base(scaleway, console) { }
    protected override IReadOnlyList<GenerativeModel> Candidates() => GenerativeModelCatalog.ByFamily("mistral");
    protected override string FamilyLabel => "Mistral";
}

/// <summary><c>pks claude qwen</c> — Qwen versions on Scaleway.</summary>
[Description("Run Claude Code on a Qwen model (Scaleway)")]
public sealed class ClaudeQwenCommand : ScalewayProxyCommandBase
{
    public ClaudeQwenCommand(IScalewayService scaleway, IAnsiConsole console) : base(scaleway, console) { }
    protected override IReadOnlyList<GenerativeModel> Candidates() => GenerativeModelCatalog.ByFamily("qwen");
    protected override string FamilyLabel => "Qwen";
}
