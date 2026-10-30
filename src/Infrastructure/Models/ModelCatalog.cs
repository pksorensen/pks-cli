namespace PKS.Infrastructure.Models;

public static class ModelCatalog
{
    public static IReadOnlyList<CatalogEntry> Known { get; } = new[]
    {
        new CatalogEntry
        {
            Name = "parakeet-v3",
            DisplayName = "Parakeet TDT 0.6B v3 (multilingual)",
            Version = "2025-09-01",
            Capabilities = new[] { "voice-stt" },
            Languages = new[]
            {
                "da-DK","en-US","en-GB","sv-SE","no-NO","de-DE","fr-FR","es-ES",
                "it-IT","pt-PT","nl-NL","pl-PL","ru-RU","uk-UA","ro-RO","hu-HU",
                "el-GR","cs-CZ","bg-BG","sk-SK","hr-HR","fi-FI","lt-LT","sl-SI",
                "lv-LV","et-EE","mt-MT",
            },
            ExpectedSizeBytes = 671088640L,
            SherpaModelType = "nemo_transducer",
            Files = new Dictionary<string, string>
            {
                ["encoder"] = "encoder.int8.onnx",
                ["decoder"] = "decoder.int8.onnx",
                ["joiner"]  = "joiner.int8.onnx",
                ["tokens"]  = "tokens.txt",
            },
            DownloadUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
            Sha256 = null,
        },
        new CatalogEntry
        {
            // The voiceprint model pks-agent-speaker (and the ctx meeting catalog) run on:
            // 3D-Speaker ERes2Net, 192-d, 16 kHz. One raw ONNX file, no archive. The tag
            // really is misspelled upstream ("recongition"); the correctly spelled one is a 404.
            // sherpa-onnx publishes no checksum; this one was computed on 2026-10-30 and matches
            // internal/assets/assets.go in pks-agent-speaker.
            Name = "speaker-eres2net",
            DisplayName = "3D-Speaker ERes2Net speaker embedding (VoxCeleb, 16 kHz, 192-d)",
            Version = "2024-03-01",
            Kind = CatalogEntryKind.SingleFile,
            Capabilities = new[] { "speaker-embedding" },
            Languages = Array.Empty<string>(),
            ExpectedSizeBytes = 26485263L,
            SherpaModelType = "speaker_embedding",
            Files = new Dictionary<string, string>
            {
                ["model"] = "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx",
            },
            DownloadUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx",
            Sha256 = "c59158379255ad66e161679cca6af8d52d51e389e3224ab7d7a7baae295c2db5",
        },
    };

    public static CatalogEntry? Find(string name) =>
        Known.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
