# Changelog

## 0.5.0.2

- Include owned local editions in relationship snapshots; `IncludeAlternateVersions` alone still excludes rows carrying `OwnerId`.

## 0.5.0.1

- Snapshot matching item IDs before reading version details, preventing equal-title items from moving across offset pages and falsely appearing unindexed.
- Fail closed when an item changes between the ID snapshot and its detail batch.

## 0.4.0.0

- Add opt-in `RunLibraryFlow`: after a successful sync and qualifying scan, await title normalization, movie merging, episode merging and Meilisearch's full-index task in order.
- Require unrestricted metadata writes, all four native tasks, Merge Versions12.0.1 or newer, and no independent downstream triggers. Preserve the Xtream schedule and never start a scan.
- Persist sync/scan identity and task start time before each stage. Skip completed cycles; stop on failures, cancellation, stale/ambiguous restart results or changed readiness/settings.
- Wake on saved configuration and coalesce completion events. Check busy writers and timers at handoffs; no recurring daemon or separate scheduler.
- Search task completion means the native indexing task returned; backend queue completion remains a separate Meilisearch health check. Manual or external jobs are not globally locked by this plugin.

## 0.3.0.0

- Target .NET 10 and Jellyfin 12.0 APIs; leave the published 10.11 catalog unchanged.
- Replace title heuristics with existing exact-TMDb IDs and Jellyfin's localized provider result.
- Preserve locked titles, identity, artwork and media; repair unlocked series labels and optionally fill missing overviews.
- Verify native NFO title saves, retain retryable failures, and checkpoint incremental title processing separately from enrichment.
- Replace file watching/stability polling and legacy cross-process coordination with coalesced native task completions and in-process serialization.
- Require successful indexing evidence, including conservative handling of unchanged sync history; never modify native schedules.
- Require an explicit media root on new installations; remove obsolete polling controls. Live Jellyfin 12.1 write acceptance remains pending.
- Guard parent/child writes against saved configuration changes and newly active sync/scans; reject scans that began before changing syncs completed.
- Package by version with assembly-version verification and SHA-256 output; document direct production installation after scan completion without a separate rehearsal environment.

## 0.2.0.2

- Ignore recently touched source roots that contain no STRM media while waiting for Jellyfin indexing.

## 0.2.0.1

- Retrieve exact-TMDB overviews through an ordered Jellyfin provider-language chain.
- Persist only Overview for enrichment; do not run a broad metadata or image refresh.
- Keep unavailable provider IDs retryable and record records without any synopsis as terminal.
- Preserve existing and locked metadata.
- Add optional exact-item and batch controls for guarded write proofs.

## 0.2.0.0

- Add opt-in exact-TMDB enrichment and title normalization through supported Jellyfin interfaces.
- Keep audit-only mode as the safe installation default.
- Preserve images and revalidate item path and TMDB identity before and after writes.
- Add atomic plugin-owned state checkpoints and persistent execution counts.
- Require a successful exact sync identity, stable canonical Movies/Series roots, and zero pending changed roots.
- Serialize plugin and legacy processors through a shared cross-process lock.

## 0.1.0.3

- Persist detailed enrichment and normalization shadow reports.
- Keep audit parity observable when Jellyfin logging is restricted to errors.

## 0.1.0.2

- Publish the plugin-owned state migration with machine-readable release packaging.

## 0.1.0.1

- Prefer a stable plugin-owned enrichment state path.
- Retain legacy Python state as a first-import fallback.

## 0.1.0.0

- Add audit-only Xtream sync-history watcher.
- Add enrichment and normalization dashboard tasks.
- Add legacy enrichment-state compatibility.
- Add Jellyfin configuration page and cross-platform path handling.
