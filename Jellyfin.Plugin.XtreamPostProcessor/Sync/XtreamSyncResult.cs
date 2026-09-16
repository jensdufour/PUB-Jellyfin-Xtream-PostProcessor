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

    public int? MoviesCreated { get; init; }
    public int? MoviesUpdated { get; init; }
    public int? EpisodesCreated { get; init; }
    public int? EpisodesUpdated { get; init; }
    public int? SeriesCreated { get; init; }
    public int? SeasonsCreated { get; init; }
    public int? FilesDeleted { get; init; }
    public int? SeriesDeleted { get; init; }
    public int? SeasonsDeleted { get; init; }
    public int? Errors { get; init; }

    [JsonIgnore]
    public DateTimeOffset? RequiredScanAfter { get; set; }

    [JsonIgnore]
    public bool KnownUnchanged => Success && string.IsNullOrWhiteSpace(Error) && Errors == 0
        && MoviesCreated == 0 && MoviesUpdated == 0 && EpisodesCreated == 0 && EpisodesUpdated == 0
        && SeriesCreated == 0 && SeasonsCreated == 0 && FilesDeleted == 0 && SeriesDeleted == 0 && SeasonsDeleted == 0;

    public string Identity => $"{StartTime:O}|{EndTime:O}";
}
