using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.XtreamPostProcessor.State;

internal sealed class EnrichmentState
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset? UpdatedUtc { get; set; }

    [JsonPropertyName("items")]
    public Dictionary<string, EnrichmentStateItem> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class EnrichmentStateItem
{
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("attemptedUtc")]
    public DateTimeOffset? AttemptedUtc { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("terminal")]
    public bool? Terminal { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
