namespace PKS.Infrastructure.Services.Claude;

public interface IClaudeManagedSettingsRenderer
{
    string Render(ClaudeMarketplaceConfiguration config);
}
