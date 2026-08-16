using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.XtreamPostProcessor.State;

internal sealed class EnrichmentState
{
    [JsonPropertyName("items")]
    public Dictionary<string, EnrichmentStateItem> Items { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class EnrichmentStateItem
{
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}
