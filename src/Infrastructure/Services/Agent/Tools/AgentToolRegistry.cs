using System.Collections.Generic;
using System.Linq;

namespace PKS.Infrastructure.Services.Agent.Tools;

public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _byName;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _byName = tools.ToDictionary(t => t.Definition.Name);
    }

    public IReadOnlyList<IAgentTool> All => _byName.Values.ToList();

    public IAgentTool GetByName(string name) =>
        _byName.TryGetValue(name, out var t) ? t : throw new KeyNotFoundException($"unknown tool: {name}");

    public AgentToolRegistry FilterTo(IEnumerable<string> allowedNames)
    {
        var allowed = new HashSet<string>(allowedNames);
        return new AgentToolRegistry(_byName.Where(kv => allowed.Contains(kv.Key)).Select(kv => kv.Value));
    }
}
