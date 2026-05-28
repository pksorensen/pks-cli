namespace PKS.Infrastructure.Services.Images;

public record ImageGenerationRequest(
    string Prompt,
    string Model,
    string AspectRatio,
    string? Resolution,
    string? InputImagePath);

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
