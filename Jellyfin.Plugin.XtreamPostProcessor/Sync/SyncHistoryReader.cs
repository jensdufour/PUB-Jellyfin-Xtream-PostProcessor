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

        var ordered = results?
            .Where(result => result.StartTime != default && result.EndTime != default)
            .OrderByDescending(result => result.EndTime).ToArray();
        if (ordered is null || ordered.Length == 0) return null;
        var latest = ordered[0];
        latest.RequiredScanAfter = ordered.FirstOrDefault(result => !result.KnownUnchanged)?.EndTime ?? ordered[^1].StartTime;
        return latest;
    }
}
