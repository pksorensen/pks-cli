using PKS.Commands.Runtime;

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var configuredPort)
    ? configuredPort
    : 8080;

return await PksRuntimeHost.RunAsync(port);
