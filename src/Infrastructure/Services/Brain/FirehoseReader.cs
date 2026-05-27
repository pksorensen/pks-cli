using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// Default <see cref="IFirehoseReader"/> implementation — streams rows from the
/// global firehose JSONL files with a cheap session-id pre-filter.
///
/// Extracted from <see cref="BrainExtractContextBuilder"/> so the commit-plan
/// pipeline can share the same fast path.
/// </summary>
public sealed class FirehoseReader : IFirehoseReader
{
    private readonly IBrainPathResolver _paths;

    public FirehoseReader(IBrainPathResolver paths)
    {
        _paths = paths;
    }

    public async IAsyncEnumerable<T> ReadAsync<T>(
        BrainFirehose firehose,
        string? sessionId,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : class
    {
        var path = _paths.GlobalFirehose(firehose);
        if (!File.Exists(path)) yield break;

        // sessionId is part of every row; cheap pre-filter via Contains avoids
        // parsing every row when the file is huge.
        var needle = sessionId is null
            ? null
            : "\"sessionId\":\"" + sessionId + "\"";

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) continue;
            if (needle is not null && !line.Contains(needle, System.StringComparison.Ordinal)) continue;

            T? row;
            try
            {
                row = JsonSerializer.Deserialize<T>(line, BrainIndexStore.JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (row is not null) yield return row;
        }
    }
}
