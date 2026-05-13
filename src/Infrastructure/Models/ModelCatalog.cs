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
    };

    public static CatalogEntry? Find(string name) =>
        Known.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
