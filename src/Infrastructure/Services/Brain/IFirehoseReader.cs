using System.Collections.Generic;
using System.Threading;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// Streams typed rows out of the global firehose JSONL files
/// (`~/.pks-cli/brain/{prompts|tools|files|errors}.jsonl`).
///
/// Implementations apply a cheap pre-filter on the raw line before JSON parsing
/// when a <c>sessionId</c> is supplied, so callers can scan a huge file without
/// materialising every row.
/// </summary>
public interface IFirehoseReader
{
    /// <summary>
    /// Read rows from the given firehose.
    /// </summary>
    /// <param name="firehose">Which firehose to read.</param>
    /// <param name="sessionId">
    /// Optional. When supplied, only rows whose JSON contains
    /// <c>"sessionId":"&lt;sessionId&gt;"</c> are parsed and yielded.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<T> ReadAsync<T>(BrainFirehose firehose, string? sessionId, CancellationToken ct = default)
        where T : class;
}
