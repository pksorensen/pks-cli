using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Implementations;

/// <summary>
/// DEPRECATED: This initializer is no longer used
/// Use 'pks hooks init' command instead for Claude Code hooks integration
/// </summary>
public class HooksInitializer : BaseInitializer
{
    public override string Id => "hooks";
    public override string Name => "Hooks System (Deprecated)";
    public override string Description => "DEPRECATED: Use 'pks hooks init' command instead";
    public override int Order => 1000; // Very low priority

    public override async Task<bool> ShouldRunAsync(InitializationContext context)
    {
        // Never run - this initializer is deprecated
        return false;
    }

    protected override async Task<InitializationResult> ExecuteInternalAsync(InitializationContext context)
    {
        // No-op - deprecated
        return InitializationResult.CreateSuccess("Hooks initializer is deprecated - use 'pks hooks init' instead");
    }

    public override IEnumerable<InitializerOption> GetOptions()
    {
        // No options for deprecated initializer
        return Enumerable.Empty<InitializerOption>();
    }
}