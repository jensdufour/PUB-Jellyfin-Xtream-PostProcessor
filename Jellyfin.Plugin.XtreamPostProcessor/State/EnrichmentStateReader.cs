using System.Text.Json;

namespace Jellyfin.Plugin.XtreamPostProcessor.State;

/// <summary>
/// Reads enrichment state produced by the legacy pipeline.
/// </summary>
public sealed class EnrichmentStateReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal async Task<EnrichmentState> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new EnrichmentState();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EnrichmentState>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ?? new EnrichmentState();
    }
}
