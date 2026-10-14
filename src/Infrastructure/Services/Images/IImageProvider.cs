namespace PKS.Infrastructure.Services.Images;

public record ImageGenerationRequest(
    string Prompt,
    string Model,
    string AspectRatio,
    string? Resolution,
    IReadOnlyList<string> InputImagePaths,
    string? Quality = null,
    string? Size = null)
{
    /// <summary>The first input image, for providers that accept only one.</summary>
    public string? InputImagePath => InputImagePaths.Count > 0 ? InputImagePaths[0] : null;
}

public record ImageModelInfo(string Name, string DisplayName, string Description);

public interface IImageProvider
{
    string Name { get; }

    Task<bool> IsAuthenticatedAsync();

    Task<bool> CanServeModelAsync(string model);

    Task<IReadOnlyList<ImageModelInfo>> ListModelsAsync();

    Task<byte[]> GenerateAsync(ImageGenerationRequest request);

    string AuthHint { get; }
}
