using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Images;

/// <summary>
/// Image provider backed by Azure AI Foundry deployments (gpt-image-2, dall-e-*).
/// Uses the stored Foundry endpoint + API key from <see cref="IAzureFoundryAuthService"/>.
/// </summary>
public class AzureFoundryImageProvider : IImageProvider
{
    private const string ApiVersion = "2024-02-01";

    private readonly IAzureFoundryAuthService _auth;
    private readonly HttpClient _http;

    public AzureFoundryImageProvider(IAzureFoundryAuthService auth, HttpClient http)
    {
        _auth = auth;
        _http = http;
    }

    public string Name => "foundry";

    public string AuthHint => "Run: pks foundry init (then ensure an image model deployment exists, e.g. gpt-image-2)";

    private const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    public async Task<bool> IsAuthenticatedAsync()
    {
        var creds = await _auth.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrWhiteSpace(creds.SelectedResourceEndpoint))
            return false;

        // Either an API key OR an OAuth refresh token is enough to authenticate.
        return !string.IsNullOrWhiteSpace(creds.ApiKey)
            || !string.IsNullOrWhiteSpace(creds.RefreshToken);
    }

    public async Task<bool> CanServeModelAsync(string model)
    {
        var creds = await _auth.GetStoredCredentialsAsync();
        if (creds == null)
            return false;

        if (creds.EnabledModels.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Fall back to a name-prefix heuristic so newly added deployments still work.
        return LooksLikeFoundryImageModel(model);
    }

    public async Task<IReadOnlyList<ImageModelInfo>> ListModelsAsync()
    {
        var creds = await _auth.GetStoredCredentialsAsync();
        if (creds == null)
            return Array.Empty<ImageModelInfo>();

        return creds.EnabledModels
            .Where(LooksLikeFoundryImageModel)
            .Select(m => new ImageModelInfo(m, m, $"Azure Foundry deployment ({creds.SelectedResourceName})"))
            .ToList();
    }

    public async Task<byte[]> GenerateAsync(ImageGenerationRequest request)
    {
        var creds = await _auth.GetStoredCredentialsAsync()
            ?? throw new InvalidOperationException("No Azure Foundry credentials. Run: pks foundry init");

        if (string.IsNullOrWhiteSpace(creds.SelectedResourceEndpoint))
            throw new InvalidOperationException("Foundry credentials have no endpoint selected. Re-run: pks foundry init");

        var endpoint = creds.SelectedResourceEndpoint.TrimEnd('/');
        var isEdit = !string.IsNullOrEmpty(request.InputImagePath);
        var path = isEdit ? "images/edits" : "images/generations";
        // gpt-image-* edits (images/edits) require the 2025 preview api-version;
        // the older default still serves generations. Use the right one per route.
        var apiVersion = isEdit ? "2025-04-01-preview" : ApiVersion;
        var url = $"{endpoint}/openai/deployments/{request.Model}/{path}?api-version={apiVersion}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);

        // Prefer an explicit API key; otherwise mint a Bearer token from the OAuth refresh token.
        if (!string.IsNullOrWhiteSpace(creds.ApiKey))
        {
            req.Headers.TryAddWithoutValidation("api-key", creds.ApiKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey);
        }
        else
        {
            var token = await _auth.GetAccessTokenAsync(CognitiveServicesScope)
                ?? throw new InvalidOperationException(
                    "Failed to acquire a Cognitive Services access token from Foundry credentials. Re-run: pks foundry init");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (isEdit)
        {
            var form = new MultipartFormDataContent();
            var imageBytes = await File.ReadAllBytesAsync(request.InputImagePath!);
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(request.InputImagePath!));
            form.Add(imageContent, "image", Path.GetFileName(request.InputImagePath!));
            form.Add(new StringContent(request.Prompt), "prompt");
            form.Add(new StringContent("1"), "n");
            var (editW, editH) = ResolveSize(request.AspectRatio, request.Resolution);
            form.Add(new StringContent($"{editW}x{editH}"), "size");
            req.Content = form;
        }
        else
        {
            var (w, h) = ResolveSize(request.AspectRatio, request.Resolution);
            var body = new
            {
                prompt = request.Prompt,
                size = $"{w}x{h}",
                n = 1,
                output_format = "png",
            };
            req.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
        }

        var response = await _http.SendAsync(req);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Foundry image API error {(int)response.StatusCode}: {content}");

        using var doc = JsonDocument.Parse(content);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            throw new InvalidOperationException("Foundry response contained no image data");

        var first = data[0];

        if (first.TryGetProperty("b64_json", out var b64Prop))
        {
            var b64 = b64Prop.GetString()
                ?? throw new InvalidOperationException("Foundry response b64_json was empty");
            return Convert.FromBase64String(b64);
        }

        if (first.TryGetProperty("url", out var urlProp))
        {
            var imageUrl = urlProp.GetString()
                ?? throw new InvalidOperationException("Foundry response url was empty");
            return await _http.GetByteArrayAsync(imageUrl);
        }

        throw new InvalidOperationException("Foundry response had neither b64_json nor url");
    }

    private static bool LooksLikeFoundryImageModel(string model)
        => model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase);

    private static (int Width, int Height) ResolveSize(string aspectRatio, string? resolution)
    {
        // gpt-image-2 / dall-e-3 supported sizes: 1024x1024, 1792x1024, 1024x1792.
        var ratio = (aspectRatio ?? "auto").ToLowerInvariant();
        return ratio switch
        {
            "16:9" or "4:3" => (1792, 1024),
            "9:16" or "3:4" => (1024, 1792),
            _ => ParseSquare(resolution),
        };
    }

    private static (int, int) ParseSquare(string? resolution)
    {
        if (!string.IsNullOrWhiteSpace(resolution) && int.TryParse(resolution, out var n) && n > 0)
            return (n, n);
        return (1024, 1024);
    }

    private static string GuessMimeType(string path)
        => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
}
