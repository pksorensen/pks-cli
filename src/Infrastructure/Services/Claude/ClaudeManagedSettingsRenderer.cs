using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Claude;

public class ClaudeManagedSettingsRenderer : IClaudeManagedSettingsRenderer
{
    public string Render(ClaudeMarketplaceConfiguration config)
    {
        var root = new JsonObject();

        // extraKnownMarketplaces — only if there are any marketplaces
        if (config.Marketplaces.Count > 0)
        {
            var ekm = new JsonObject();
            foreach (var marketplace in config.Marketplaces)
            {
                var mktObj = new JsonObject();
                var srcObj = new JsonObject
                {
                    ["source"] = marketplace.Source.SourceType
                };
                if (marketplace.Source.Url != null)
                    srcObj["url"] = marketplace.Source.Url;
                if (marketplace.Source.Repo != null)
                    srcObj["repo"] = marketplace.Source.Repo;
                if (marketplace.Source.Ref != null)
                    srcObj["ref"] = marketplace.Source.Ref;
                if (marketplace.Source.Path != null)
                    srcObj["path"] = marketplace.Source.Path;

                mktObj["source"] = srcObj;
                ekm[marketplace.Id] = mktObj;
            }
            root["extraKnownMarketplaces"] = ekm;
        }

        // enabledPlugins — only if any plugins are enabled
        var enabledPlugins = config.Marketplaces
            .SelectMany(m => m.Plugins
                .Where(p => p.Enabled)
                .Select(p => (Key: $"{p.Name}@{m.Id}", Value: true)))
            .ToList();

        if (enabledPlugins.Count > 0)
        {
            var ep = new JsonObject();
            foreach (var (key, value) in enabledPlugins)
                ep[key] = value;
            root["enabledPlugins"] = ep;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
