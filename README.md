# Xtream Post Processor for Jellyfin

Portable Jellyfin plugin that watches Xtream Library synchronization and exposes
separate metadata-enrichment and title-normalization tasks in the Jellyfin
dashboard.

Audit-only is the default. Write mode uses Jellyfin's supported provider,
library, and metadata-saver interfaces; it never modifies media files.

## Features

- Cross-platform `FileSystemWatcher` for `xtream-library/sync_history.json`.
- Dashboard tasks for enrichment and normalization audits or writes.
- Detailed audit JSON under Jellyfin's `data/xtream-post-processor` directory.
- Timestamp-based sync selection that covers manual and scheduled Xtream runs.
- Compatible reads of the legacy state and atomic plugin-owned checkpoints.
- Provider-prefix, metadata-ID, year/country, and known-series title rules.
- No title-specific media handling or Futurama special case.
- Safe default: automatic watcher enabled, writes disabled.

## Install

Add this custom repository in Jellyfin's plugin settings:

```text
https://jensdufour.github.io/PUB-Jellyfin-Xtream-PostProcessor/manifest.json
```

Install **Xtream Post Processor** from the catalog and restart Jellyfin. The
tasks appear under **Dashboard > Scheduled Tasks > Xtream Post Processor**.

## Configuration

The configuration page supports paths relative to Jellyfin's data directory or
absolute paths. Defaults match a standard Xtream Library installation:

| Setting | Default |
| --- | --- |
| Sync history | `xtream-library/sync_history.json` |
| Legacy state | `xtream-metadata-enrichment.json` |
| Plugin state | `xtream-post-processor/enrichment-state.json` |
| Media root | `/data/media/xtream` |
| Fallback languages | `nl,en,sv,da,cs` |
| Write batch size | `0` (all candidates) |
| Write item ID | empty |
| Audit only | `true` |

## Architecture

- `XtreamSyncWatcher` debounces sync-history changes and queues enrichment.
- `EnrichXtreamTask` audits or applies exact-TMDB enrichment candidates.
- Enrichment tries configured languages in order and changes only a missing Overview.
- Unavailable IDs remain retryable; records with no synopsis in any configured language are terminal.
- `NormalizeXtreamTask` audits or applies title changes after enrichment succeeds.
- `ILibraryManager` supplies Movies and Series; no direct SQLite access is used.
- Write mode runs only after a successful latest sync and preserves images.
- Write mode requires every changed source root from that sync to be indexed.
- A shared lock prevents overlap with the migration-era Python pipeline.
- Plugin state is atomically replaced under Jellyfin's data directory.

## Build

Requires .NET SDK 9.

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release --no-build
```

## Production Status

Version `0.2.0.1` replaced the migration-era Python/systemd pipeline after
one-item, full controlled, and official scheduled-sync proofs on Jellyfin
`10.11.11`. Audit-only remains the installation default for new servers.

## License

GPL-3.0.
