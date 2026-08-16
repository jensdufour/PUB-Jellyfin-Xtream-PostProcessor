using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.XtreamPostProcessor.Sync;

internal sealed class XtreamSyncResult
{
    [JsonPropertyName("StartTime")]
    public DateTimeOffset StartTime { get; init; }

    [JsonPropertyName("EndTime")]
    public DateTimeOffset EndTime { get; init; }

    [JsonPropertyName("Success")]
    public bool Success { get; init; }

    [JsonPropertyName("Error")]
    public string? Error { get; init; }

    [JsonPropertyName("WasIncrementalSync")]
    public bool WasIncrementalSync { get; init; }

    public string Identity => $"{StartTime:O}|{EndTime:O}";
}
