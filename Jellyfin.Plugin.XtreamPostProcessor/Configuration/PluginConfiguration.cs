using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.XtreamPostProcessor.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    private string? _fallbackLanguages;

    /// <summary>Gets or sets a value indicating whether automatic processing is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether changes are audit-only.</summary>
    public bool AuditOnly { get; set; } = true;

    /// <summary>Gets or sets the sync-history path relative to Jellyfin's data directory.</summary>
    public string SyncHistoryRelativePath { get; set; } = "xtream-library/sync_history.json";

    /// <summary>Gets or sets the legacy enrichment-state path relative to Jellyfin's data directory.</summary>
    public string LegacyStateRelativePath { get; set; } = "xtream-metadata-enrichment.json";

    /// <summary>Gets or sets the plugin-owned enrichment-state path relative to Jellyfin's data directory.</summary>
    public string StateRelativePath { get; set; } = "xtream-post-processor/enrichment-state.json";

    /// <summary>Gets or sets the Xtream media root.</summary>
    public string XtreamRoot { get; set; } = "/data/media/xtream";

    /// <summary>Gets or sets the legacy fallback metadata language.</summary>
    public string FallbackLanguage { get; set; } = "nl";

    /// <summary>Gets or sets the comma-separated fallback metadata languages.</summary>
    public string FallbackLanguages
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_fallbackLanguages))
            {
                return _fallbackLanguages;
            }

            var legacy = (FallbackLanguage ?? string.Empty).Trim();
            if (legacy.Length == 0)
            {
                return "nl,en,sv,da,cs";
            }

            return string.Equals(legacy, "nl", StringComparison.OrdinalIgnoreCase)
                ? "nl,en,sv,da,cs"
                : $"{legacy},nl,en,sv,da,cs";
        }

        set => _fallbackLanguages = value;
    }

    /// <summary>Gets or sets the maximum items written per run, or zero for unlimited.</summary>
    public int WriteBatchSize { get; set; }

    /// <summary>Gets or sets an optional Jellyfin item ID to process exclusively.</summary>
    public string WriteItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum enrichment concurrency.</summary>
    public int EnrichmentWorkers { get; set; } = 6;

    /// <summary>Gets or sets a value indicating whether retryable failures are included.</summary>
    public bool RetryFailed { get; set; } = true;

    /// <summary>Gets or sets the file-watcher debounce interval in seconds.</summary>
    public int WatchDebounceSeconds { get; set; } = 2;

    /// <summary>Gets or sets the indexing stability window in seconds.</summary>
    public int IndexingStableSeconds { get; set; } = 180;

    /// <summary>Gets or sets the indexing timeout in seconds.</summary>
    public int IndexingTimeoutSeconds { get; set; } = 5400;

    /// <summary>Gets or sets the maximum changed source roots not represented in Jellyfin.</summary>
    public int MaxUnindexedChangedRoots { get; set; } = 100;
}
