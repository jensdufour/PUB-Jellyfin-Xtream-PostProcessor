# Changelog

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
