# Xtream Post Processor for Jellyfin

Portable Jellyfin plugin that watches Xtream Library synchronization and exposes
separate metadata-enrichment and title-normalization tasks in the Jellyfin
dashboard.

> Version `0.1.x` is audit-only. It computes and logs the same candidate plans as
> the established Python pipeline but never writes metadata or media files.

## Features

- Cross-platform `FileSystemWatcher` for `xtream-library/sync_history.json`.
- Dashboard tasks for enrichment and normalization audits.
- Detailed audit JSON under Jellyfin's `data/xtream-post-processor` directory.
- Timestamp-based sync selection that covers manual and scheduled Xtream runs.
- Compatible reads of the legacy `xtream-metadata-enrichment.json` state.
- Provider-prefix, metadata-ID, year/country, and known-series title rules.
- No title-specific media handling or Futurama special case.
- Safe default: automatic watcher enabled, writes disabled.

## Install

Add this custom repository in Jellyfin's plugin settings:

```text
https://raw.githubusercontent.com/jensdufour/PUB-Jellyfin-Xtream-PostProcessor/main/manifest.json
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
| Fallback language | `nl` |
| Audit only | Always enabled in `0.1.x` |

## Architecture

- `XtreamSyncWatcher` debounces sync-history changes and queues enrichment.
- `EnrichXtreamTask` audits exact-TMDB enrichment candidates.
- `NormalizeXtreamTask` audits title changes after enrichment succeeds.
- `ILibraryManager` supplies Movies and Series; no direct SQLite access is used.
- Plugin state belongs under Jellyfin's plugin data path in future write-enabled releases.

## Build

Requires .NET SDK 9.

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release --no-build
```

## Migration Status

The production Python/systemd pipeline remains authoritative while `0.1.x` runs
in shadow mode. Cutover requires matching candidate counts for both a manual and
scheduled Xtream sync, followed by a write-enabled release with rollback proof.

## License

GPL-3.0.
