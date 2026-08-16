using System.Text.Json;

namespace Jellyfin.Plugin.XtreamPostProcessor.Sync;

/// <summary>
/// Reads Xtream synchronization history.
/// </summary>
public sealed class SyncHistoryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal async Task<XtreamSyncResult?> ReadLatestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var results = await JsonSerializer.DeserializeAsync<List<XtreamSyncResult>>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return results?
            .Where(result => result.StartTime != default && result.EndTime != default)
            .MaxBy(result => result.EndTime);
    }
}
