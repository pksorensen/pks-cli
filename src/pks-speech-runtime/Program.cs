using PKS.Commands.Runtime.Speech;

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var configuredPort)
    ? configuredPort
    : 8080;

return await SpeechRuntimeCommand.RunAsync(port);
