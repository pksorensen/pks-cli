using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Security;

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
        return creds.ApiKey.HasValue || creds.RefreshToken.HasValue || creds.IsManagedIdentity;
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
        if (creds.ApiKey.HasValue)
        {
            req.Headers.TryAddWithoutValidation("api-key", creds.ApiKey.Reveal());
            SecretSink.SetBearerToken(req, creds.ApiKey);
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
            // Several references go as repeated image[] parts (e.g. a scene photo plus
            // the artwork to place in it); a single one keeps the plain "image" field.
            var field = request.InputImagePaths.Count > 1 ? "image[]" : "image";
            foreach (var input in request.InputImagePaths)
            {
                var imageContent = new ByteArrayContent(await File.ReadAllBytesAsync(input));
                imageContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(input));
                form.Add(imageContent, field, Path.GetFileName(input));
            }
            form.Add(new StringContent(request.Prompt), "prompt");
            form.Add(new StringContent("1"), "n");
            var (editW, editH) = ResolveSize(request);
            form.Add(new StringContent($"{editW}x{editH}"), "size");
            if (!string.IsNullOrWhiteSpace(request.Quality))
                form.Add(new StringContent(request.Quality!), "quality");
            req.Content = form;
        }
        else
        {
            var (w, h) = ResolveSize(request);
            // quality is optional: omitting it lets the deployment pick its default
            // (the top tier), which is ~30x the cost of "low" for draft sweeps.
            var body = new Dictionary<string, object>
            {
                ["prompt"] = request.Prompt,
                ["size"] = $"{w}x{h}",
                ["n"] = 1,
                ["output_format"] = "png",
            };
            if (!string.IsNullOrWhiteSpace(request.Quality))
                body["quality"] = request.Quality!;
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

    // gpt-image-2 takes any size whose edges are multiples of 16, the long edge at most
    // 3840, the ratio at most 3:1 and the area within these bounds. dall-e-* deployments
    // only know the three classic sizes, so they keep the old mapping.
    private const int MaxEdge = 3840;
    private const int MinPixels = 655_360;
    private const int MaxPixels = 8_294_400;
    private const int DefaultLongEdge = 1536;

    internal static (int Width, int Height) ResolveSize(ImageGenerationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Size))
            return ParseSize(request.Size!);

        if (!request.Model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase))
            return ResolveClassicSize(request.AspectRatio, request.Resolution);

        var ratio = ParseRatio(request.AspectRatio);
        // "auto" on an edit follows the first input, so a photo keeps its framing.
        if (ratio == null && request.InputImagePath != null && ImageDimensions.TryRead(request.InputImagePath, out var iw, out var ih))
            ratio = (double)iw / ih;
        if (ratio == null)
            return ParseSquare(request.Resolution);

        var longEdge = int.TryParse(request.Resolution, out var r) && r > 0 ? r : DefaultLongEdge;
        return FitSize(ratio.Value, longEdge);
    }

    internal static (int Width, int Height) FitSize(double ratio, int longEdge)
    {
        ratio = Math.Clamp(ratio, 1.0 / 3, 3.0);
        longEdge = Math.Min(longEdge, MaxEdge);
        double w = ratio >= 1 ? longEdge : longEdge * ratio;
        double h = ratio >= 1 ? longEdge / ratio : longEdge;

        var area = w * h;
        var scale = area > MaxPixels ? Math.Sqrt(MaxPixels / area) : area < MinPixels ? Math.Sqrt(MinPixels / area) : 1;
        w *= scale;
        h *= scale;

        int width = Snap(w), height = Snap(h);
        while ((long)width * height > MaxPixels) { width -= 16; height = Snap(width / ratio); }
        while ((long)width * height < MinPixels) { width += 16; height = Snap(width / ratio); }
        return (width, height);
    }

    private static int Snap(double v) => Math.Clamp((int)Math.Round(v / 16) * 16, 16, MaxEdge);

    private static double? ParseRatio(string? aspectRatio)
    {
        var parts = (aspectRatio ?? "auto").Split(':');
        return parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)
            && a > 0 && b > 0
            ? a / b
            : null;
    }

    private static (int, int) ParseSize(string size)
    {
        var parts = size.ToLowerInvariant().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
            return (w, h);
        throw new InvalidOperationException($"--size must look like 2048x1360, got '{size}'");
    }

    private static (int Width, int Height) ResolveClassicSize(string aspectRatio, string? resolution)
    {
        // dall-e-3 supported sizes: 1024x1024, 1792x1024, 1024x1792.
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
