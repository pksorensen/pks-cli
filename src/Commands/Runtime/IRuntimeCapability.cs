using Microsoft.AspNetCore.Builder;

namespace PKS.Commands.Runtime;

internal interface IRuntimeCapability
{
    string Id { get; }
    object Describe();
    void MapEndpoints(WebApplication app, string serviceToken);
}
