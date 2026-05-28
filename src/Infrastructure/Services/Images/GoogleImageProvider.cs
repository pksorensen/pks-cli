namespace PKS.Infrastructure.Services.Images;

public class GoogleImageProvider : IImageProvider
{
    private readonly IGoogleAiService _google;

    public GoogleImageProvider(IGoogleAiService google)
    {
        _google = google;
    }

    public string Name => "google";

    public string AuthHint => "Run: pks google init";

    public Task<bool> IsAuthenticatedAsync() => _google.IsAuthenticatedAsync();

    public async Task<bool> CanServeModelAsync(string model)
    {
        if (LooksLikeGoogleModel(model))
            return true;

        // Best-effort check against the live/known list.
        var models = await _google.ListImageModelsAsync();
        return models.Any(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ImageModelInfo>> ListModelsAsync()
    {
        var models = await _google.ListImageModelsAsync();
        return models
            .Select(m => new ImageModelInfo(m.Name, m.DisplayName, m.Description))
            .ToList();
    }

    public Task<byte[]> GenerateAsync(ImageGenerationRequest request)
        => _google.GenerateImageAsync(
            request.Prompt,
            request.Model,
            request.AspectRatio,
            request.Resolution,
            request.InputImagePath);

    private static bool LooksLikeGoogleModel(string model)
        => model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("imagen", StringComparison.OrdinalIgnoreCase);
}
