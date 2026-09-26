using PKS.Commands.Runtime.Speech;
using Xunit;

namespace PKS.CLI.Tests.Commands.Runtime;

public class SpeechProtocolTests
{
    [Fact]
    public void Parses_Default_24Khz_Mono_Pcm16_Session()
    {
        var session = SpeechProtocol.ParseSessionStart("""{"type":"session.start","capability":"speech.realtime.transcribe"}""");

        Assert.Equal(24000, session.SampleRate);
        Assert.Equal(1, session.Channels);
        Assert.Equal("pcm16", session.Encoding);
    }

    [Fact]
    public void Rejects_Unsupported_Audio_Format()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SpeechProtocol.ParseSessionStart(
            """{"type":"session.start","audio":{"sampleRate":16000,"channels":1,"encoding":"pcm16"}}"""));

        Assert.Contains("24 kHz mono PCM16", error.Message);
    }

    [Fact]
    public void Translates_Azure_Final_Transcript()
    {
        var translated = AzureVoiceLiveEvents.Translate(
            """{"type":"conversation.item.input_audio_transcription.completed","transcript":"hello factory"}""");

        Assert.NotNull(translated);
        Assert.Equal("transcript.final", translated.Type);
    }

    [Fact]
    public void Validates_Runtime_Configuration()
    {
        var values = new Dictionary<string, string?>
        {
            ["PKS_FOUNDRY_ENDPOINT"] = "https://factory.services.ai.azure.com",
            ["PKS_FOUNDRY_TRANSCRIPTION_MODEL"] = "gpt-4o-transcribe-diarize",
        };

        var options = SpeechRuntimeOptions.FromEnvironment(key => values.GetValueOrDefault(key));

        Assert.Equal("factory.services.ai.azure.com", options.FoundryEndpoint.Host);
        Assert.Equal("azure-openai-realtime-transcription", options.Provider);
        Assert.Equal("gpt-4o-transcribe-diarize", options.TranscriptionModel);
    }
}
