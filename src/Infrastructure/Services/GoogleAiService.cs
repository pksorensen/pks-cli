using System.Net.Http.Json;
using System.Text.Json;

namespace PKS.Infrastructure.Services;

public class GoogleAiModel
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
}

public interface IGoogleAiService
{
    Task<bool> IsAuthenticatedAsync();
    Task StoreApiKeyAsync(string apiKey);
    Task<string?> GetApiKeyAsync();
    Task<string?> GetRegisteredAtAsync();
    Task<bool> ValidateApiKeyAsync(string apiKey);
    Task<List<GoogleAiModel>> ListImageModelsAsync();
    Task<byte[]> GenerateImageAsync(string prompt, string model, string aspectRatio, string? resolution, string? inputImagePath = null);
}

public class GoogleAiService : IGoogleAiService
{
    private const string KeyApiKey = "google:api_key";
    private const string KeyRegisteredAt = "google:registered_at";
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    // Gemini image models available via AI Studio API key.
    // Imagen models (imagen-3.x) require Vertex AI, not an AI Studio key.
    private static readonly List<GoogleAiModel> KnownImageModels =
    [
        new() { Name = "gemini-3.1-flash-image-preview",          DisplayName = "Gemini 3.1 Flash Image Preview", Description = "Latest Gemini image generation model" },
        new() { Name = "gemini-2.0-flash-preview-image-generation", DisplayName = "Gemini 2.0 Flash Image", Description = "Gemini 2.0 native image generation" },
    ];

    private readonly IConfigurationService _config;
    private readonly HttpClient _http;

    public GoogleAiService(IConfigurationService config, HttpClient http)
    {
        _config = config;
        _http = http;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var key = await _config.GetAsync(KeyApiKey);
        return !string.IsNullOrWhiteSpace(key);
    }

    public async Task StoreApiKeyAsync(string apiKey)
    {
        await _config.SetAsync(KeyApiKey, apiKey, global: true);
        await _config.SetAsync(KeyRegisteredAt, DateTime.UtcNow.ToString("O"), global: true);
    }

    public Task<string?> GetApiKeyAsync() => _config.GetAsync(KeyApiKey);

    public Task<string?> GetRegisteredAtAsync() => _config.GetAsync(KeyRegisteredAt);

    public async Task<bool> ValidateApiKeyAsync(string apiKey)
    {
        try
        {
            var response = await _http.GetAsync($"{BaseUrl}/models?key={apiKey}");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<GoogleAiModel>> ListImageModelsAsync()
    {
        var key = await _config.GetAsync(KeyApiKey);
        if (string.IsNullOrWhiteSpace(key))
            return KnownImageModels;

        try
        {
            var response = await _http.GetAsync($"{BaseUrl}/models?key={key}");
            if (!response.IsSuccessStatusCode)
                return KnownImageModels;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var models = new List<GoogleAiModel>();

            if (json.TryGetProperty("models", out var modelsArray))
            {
                foreach (var m in modelsArray.EnumerateArray())
                {
                    // Filter by name — Gemini image models contain "image" in their model id.
                    // Imagen models (imagen-3.x) are excluded: they need Vertex AI, not an AI Studio key.
                    var modelName = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var isImageModel = modelName.Contains("image", StringComparison.OrdinalIgnoreCase)
                        && !modelName.Contains("imagen", StringComparison.OrdinalIgnoreCase);

                    if (!isImageModel)
                        continue;

                    models.Add(new GoogleAiModel
                    {
                        Name = modelName.Replace("models/", ""),
                        DisplayName = m.TryGetProperty("displayName", out var dn)
                            ? dn.GetString() ?? ""
                            : "",
                        Description = m.TryGetProperty("description", out var desc)
                            ? desc.GetString() ?? ""
                            : "",
                    });
                }
            }

            return models.Count > 0 ? models : KnownImageModels;
        }
        catch
        {
            return KnownImageModels;
        }
    }

    public async Task<byte[]> GenerateImageAsync(string prompt, string model, string aspectRatio, string? resolution, string? inputImagePath = null)
    {
        var key = await _config.GetAsync(KeyApiKey);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Google AI API key registered. Run: pks google init");

        return await GenerateViaGeminiAsync(prompt, model, key, inputImagePath);
    }

    private async Task<byte[]> GenerateViaGeminiAsync(string prompt, string model, string key, string? inputImagePath)
    {
        var url = $"{BaseUrl}/models/{model}:generateContent?key={key}";

        // Build parts: always include the text prompt.
        // When an input image is provided, include it as inlineData so the model edits/augments it.
        object body;

        if (!string.IsNullOrEmpty(inputImagePath))
        {
            var imageBytes = await File.ReadAllBytesAsync(inputImagePath);
            var b64 = Convert.ToBase64String(imageBytes);
            var mimeType = inputImagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

            body = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new { inlineData = new { mimeType, data = b64 } },
                        }
                    }
                },
                generationConfig = new { responseModalities = new[] { "IMAGE" } }
            };
        }
        else
        {
            body = new
            {
                contents = new[]
                {
                    new { parts = new object[] { new { text = prompt } } }
                },
                generationConfig = new { responseModalities = new[] { "IMAGE" } }
            };
        }

        var response = await _http.PostAsJsonAsync(url, body);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gemini API error {(int)response.StatusCode}: {content}");

        var json = JsonSerializer.Deserialize<JsonElement>(content);
        var parts = json
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts");

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("inlineData", out var inlineData))
            {
                var b64 = inlineData.GetProperty("data").GetString()
                    ?? throw new InvalidOperationException("No image data in response");

                return Convert.FromBase64String(b64);
            }
        }

        throw new InvalidOperationException("No image found in Gemini response");
    }
}
