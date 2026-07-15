using System.Text.Json;
using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

public sealed class PersonaSessionStore : IPersonaSessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IPersonaPathResolver _paths;

    public PersonaSessionStore(IPersonaPathResolver paths)
    {
        _paths = paths;
    }

    public async Task<PersonaSessionFile> LoadAsync(string contentFilePath, string locale, string? modelTag = null, CancellationToken ct = default)
    {
        var path = _paths.SessionSidecarPath(contentFilePath, locale, modelTag);
        if (!File.Exists(path))
        {
            return new PersonaSessionFile
            {
                Post = Path.GetFullPath(contentFilePath),
                Locale = locale,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Calls = new List<PersonaSessionCall>(),
            };
        }
        var raw = await File.ReadAllTextAsync(path, ct);
        var file = JsonSerializer.Deserialize<PersonaSessionFile>(raw, JsonOpts) ?? new PersonaSessionFile();
        file.Post = string.IsNullOrEmpty(file.Post) ? Path.GetFullPath(contentFilePath) : file.Post;
        file.Locale = string.IsNullOrEmpty(file.Locale) ? locale : file.Locale;
        file.Calls ??= new List<PersonaSessionCall>();
        return file;
    }

    public async Task AppendCallAsync(string contentFilePath, string locale, PersonaSessionCall call, string? modelTag = null, CancellationToken ct = default)
    {
        var path = _paths.SessionSidecarPath(contentFilePath, locale, modelTag);
        var file = await LoadAsync(contentFilePath, locale, modelTag, ct);

        file.Calls.Add(call);
        file.TotalCalls = file.Calls.Count;
        file.TotalInputTokens = file.Calls.Sum(c => c.InputTokens);
        file.TotalOutputTokens = file.Calls.Sum(c => c.OutputTokens);
        file.TotalCostUsd = file.Calls.Sum(c => c.CostUsd);
        file.UpdatedAt = DateTime.UtcNow;
        file.Post = Path.GetFullPath(contentFilePath);
        file.Locale = locale;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, JsonOpts), ct);
    }
}
