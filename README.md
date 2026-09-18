# Xtream Post Processor for Jellyfin

Portable Jellyfin 12 plugin for canonical Movie and Series titles after Xtream
Library synchronization and indexing. Version **0.4.0.0** targets .NET 10
and the Jellyfin 12.0 API baseline. The selected rollout is repository installation
on Jellyfin 12.1 when native writers are idle.
Release tags build and publish the package and catalog through GitHub Actions.
Version0.3.0 title writes and preservation checks were verified in production;
the new optional flow requires its own activation/readback.

Audit-only is the default. No daemon, external title writer, direct database
access, or additional synchronization schedule is required.

## Features

- Resolve the existing exact TMDb ID through Jellyfin's enabled `TheMovieDb`
	provider, using the item, library, then server metadata language/country.
	No local `OriginalTitle` fallback, prefix rules, aliases, or fuzzy matching.
- Preserve item IDs, provider IDs, paths, STRM content, artwork, episode titles,
	version links and watch history. Locked titles are skipped. Lock deliberate
	custom titles before enabling writes: every eligible title is canonicalized,
	not just names that look decorated.
- Save through Jellyfin's native metadata saver and library APIs. Read back the
	title from enabled NFO file savers before checkpointing; silent saver failures
	remain retryable. With no enabled NFO saver, only database metadata is updated.
- Repair unlocked Season/Episode `SeriesName` labels without recursive metadata
	refresh. Optionally fill a missing unlocked Overview from the same localized
	result; existing overviews are never replaced.
- Use one sequential engine for backlog and subsequent changes, atomic title
	checkpoints, and detailed JSON audit reports. Optionally sequence native merge
	and search tasks after successful title processing; subtitles stay independent.

## Install

Add this repository in Jellyfin and select **Xtream Post Processor 0.4.0.0**.
Earlier catalog entries remain available for Jellyfin 10.11; do not select them
on Jellyfin 12.

```text
https://jensdufour.github.io/PUB-Jellyfin-Xtream-PostProcessor/manifest.json
```

The new plugin activates at the next approved restart; downloading a repository
package does not require restarting immediately. The task names remain under
**Dashboard > Scheduled Tasks > Xtream Post Processor**.

## Post-Scan Deployment

1. Wait for the current full scan to complete successfully and for sync/metadata
	writers to be idle. The scan must have started after the last potentially
	changing sync. Preserve the database, configuration and affected NFOs through
	the existing backup procedure; an application-disk backup alone does not
	cover media on separate mounts. Do not interrupt the running scan to install.
2. Install version `0.4.0.0` through the repository and leave its restart pending
	until the current scan finishes and a restart is approved. On an upgrade,
	persist `AuditOnly=true` before startup; saved write settings override defaults.
3. Set the existing absolute Xtream media root, enable the native `TheMovieDb`
	fetcher and NFO saver for Movies/Series, then save `Enabled=true`,
	`AuditOnly=false`, `WriteBatchSize=0`, empty `WriteItemId`, and `RetryFailed=true`.
	Keep `FillMissingOverview=false` unless the same-result overview fill is wanted.
	Preserve existing native schedules and deliberate metadata locks.
4. Run **Process Xtream Title Normalization** once for the initial backlog.
	Thereafter completion events use the same incremental task. Changed syncs
	still need a qualifying full scan; the plugin never changes that scheduling.
	The separate multi-language enrichment task is not required for title cleanup.
5. Check the task's completion report and Jellyfin's resulting titles/NFOs during
	normal operation. Failures remain retryable. To stop further writes, save
	`Enabled=false` or `AuditOnly=true` and cancel the running task; the final
	write guard checks saved settings, while an already-issued save may finish.
	Keep the title checkpoint for resumption; do not clear it during a running job.

No isolated server, extra operational script, or repeated pilot is a prerequisite
for this deployment. Installation/restart remains deferred until the scan is done.

## Configuration

State/history paths may be relative to Jellyfin's data directory or absolute.
The media root must be explicitly configured as an existing absolute directory
containing `Movies` and/or `Series`. Existing saved root settings are retained.

| Setting | Default |
| --- | --- |
| Sync history | `xtream-library/sync_history.json` |
| Legacy state | `xtream-metadata-enrichment.json` |
| Plugin state | `xtream-post-processor/enrichment-state.json` |
| Title state (fixed, separate from legacy enrichment) | `xtream-post-processor/title-state.json` |
| Media root | empty (required) |
| Manual enrichment fallback languages | `nl,en,sv,da,cs` |
| Canonical-task missing Overview fill | `false` |
| Write batch size (also bounds title audit lookups) | `0` (all candidates) |
| Write item ID | empty |
| Audit only | `true` |
| Run library flow | `false` |

## Optional Ordered Flow

Enable `RunLibraryFlow` only with `Enabled=true`, `AuditOnly=false`, zero
`WriteBatchSize` and empty `WriteItemId`. Keep Xtream's existing daily sync and
post-sync scan. Remove independent triggers for title normalization, both merge
tasks and the full Meilisearch index task before enabling the option. The plugin
checks those triggers but does not change them. Merge Versions12.0.1 or newer is
required because its native task completion awaits the actual writes.

The existing watcher awaits these native tasks in order:

```text
Successful Xtream sync + qualifying full scan
	-> XtreamPostProcessorNormalize
	-> MergeMoviesTask
	-> MergeEpisodesTask
	-> task-meilisearch-reindex-full
```

The flow uses `data/xtream-post-processor/library-flow.json` for its sync/scan
identity, next-stage index, requested timestamp, status and stop reason. It saves
atomically before starting each stage and checks a fresh successful native result
before advancing. Duplicate completion events and restarts do not repeat completed
cycles. A restarted in-flight stage is accepted only if its saved native result
proves it completed after this flow requested it; otherwise the cycle stops.
Failure is not silently retried for the same cycle. After correcting the cause,
disable processing and verify all writers idle before archiving/removing this
flow checkpoint and re-enabling it to rerun the chain. Keep `title-state.json`.
A newer successful sync/scan defines a new cycle without manual reset.

Changing settings or sync/scan identity stops subsequent stages. Stopping the
flow does not roll back completed work or abandon a native save already running.
This sequences plugin-owned launches; Jellyfin provides no global lock against a
manual scan, another plugin or the next daily sync. Avoid launching competing jobs.
The full-index task's completion does not independently certify that Meilisearch's
asynchronous backend queue is empty. This does not make library scans incremental.

## Completion And Retry

`XtreamSyncWatcher` coalesces Jellyfin `TaskCompleted` notifications for
`XtreamLibrarySync` and `RefreshLibrary`. Merge/task completion wakes pending
work; startup attempts backlog recovery. Manual scheduled-task runs and native
interval runs use the same path. No task triggers or Xtream interval settings
are modified. The legacy multi-language enrichment task remains manual and is
independent of canonical title processing.

Processing requires the latest sync to have succeeded, no active sync/full scan
or recognized Merge Versions task, and a successful full library scan whose start
is at or after the last potentially changed sync's end. Explicit zero-change
counters allow reuse of a scan after an earlier changed run in the retained history. Missing counters,
errors, or missing history are not evidence of no changes. If all retained runs
are unchanged, a scan at least as recent as the oldest retained start is required.
Failed/cancelled scans never authorize writes.

**A full scan is a prerequisite after changed syncs.** File-monitor refreshes
alone do not establish this completion proof. With Xtream's full-scan option off,
processing waits for an independently scheduled or manually completed full scan.
The plugin does not enable that option or queue scans. A direct Xtream dashboard
sync is picked up on a subsequent full-scan completion, not by a history-file
watcher. Enabling the plugin takes effect on the next completion or manual task.

Checks repeat before each item and immediately before parent/child metadata
mutation, after provider/NFO reads. Saving new plugin configuration cancels the
old run before its next write; completed checkpoints remain available. The shared
semaphore serializes this plugin's two tasks only: Jellyfin exposes no global
transaction with other plugins or UI edits. In particular, Merge Versions work
that outlives its task's completion is not covered. Do not overlap external
metadata writers with an acceptance run.

Successful title checkpoints skip unchanged items. A changed name, TMDb ID,
path, language/country, optional-overview policy, or series `DateLastMediaAdded`
reopens the item. New items precede retries in bounded batches. Unavailable IDs
and failed writes retry on a later run when retrying is enabled. Missing IDs and
locked titles are left untouched. Title checkpoints are saved every 64 items
and at task exit; cancellation or a crash may replay completed items, safely.
Audit mode writes reports but never metadata or title checkpoints. It does not
mark the backlog complete, so repeated audits can repeat provider lookups.

NFO persistence follows enabled native saver paths and is not atomic with the
database update. A failed run may leave a newer NFO while its item remains old;
the next run reconciles it. No NFO or media renames are performed. A lock-only
change on an already checkpointed item's child, or an NFO-only edit not imported
by a scan, does not invalidate the parent checkpoint; with processing disabled,
back up and remove `title-state.json` to explicitly re-audit the scoped backlog.

## Build

Requires .NET SDK 10. `global.json` accepts an installed 10.0 feature band.

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release --no-build
```

The baseline Release build passed 46 tests against Jellyfin Controller/Model
`12.0.0`, including exact/localized IDs, locks, child labels, NFO saver failures,
task audit/checkpoint/retry, no-change syncs, completion coalescing and restart
recovery. The embedded settings page load/save bindings were also exercised.
No new test dependency was added. The final completion-guard changes passed the
12 existing focused checks and a Release package build. These are automated
source checks, not a live Jellyfin 12.1 deployment claim.

To regenerate the version-checked install ZIP with the existing packaging helper:

```powershell
./scripts/package.ps1 -Version 0.4.0.0
```

Packages live under ignored `dist/<version>/`; older versions are not removed.
The helper returns the catalog checksum and a SHA-256 integrity hash. Do not run
the manifest update helper or publish a release as part of a local installation.

## Historical Compatibility Build

The following records the earlier behavior-preserving 0.2.0.2 retarget, not the
current canonicalization implementation. Keep its patch/evidence separate.

The `0.2.0.2` release project used `net9.0`, Controller/Model `10.11.11`, SDK 9, and
version `0.2.0.2`. Existing catalogs, manifests, and released versions are
unchanged. `patches/postprocessor-jf12-compat.patch` is an opt-in source-copy
retarget of that historical commit, not a published release.
It changes SDK to `10.0.400`, both project frameworks to `net10.0`, Jellyfin
references to `12.0.0`, and only the copy's build metadata to ABI `12.0.0.0`.
No C# behavior or version number changes. Do not publish this copy's metadata.

From the PR-Zion root, archive the plugin submodule's pinned `0.2.0.2` commit
`2a7c4d3650649be44c9a0f207e6d92b55ee1fb1d`. Use SDK `10.0.400` /
runtime `10.0.11` on `PATH`:

```powershell
$plugin = Join-Path $PWD 'pve/108-jellyfin/plugins/xtream-post-processor'
$scratch = Join-Path $env:TEMP ('postprocessor-jf12-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $scratch | Out-Null
git -C $plugin -c core.autocrlf=false archive --format=tar "--output=$scratch/source.tar" 2a7c4d3650649be44c9a0f207e6d92b55ee1fb1d
tar -xf "$scratch/source.tar" -C $scratch
git -C $scratch apply --check "$plugin/patches/postprocessor-jf12-compat.patch"
git -C $scratch apply "$plugin/patches/postprocessor-jf12-compat.patch"
Push-Location $scratch
dotnet build --configuration Release
dotnet test --configuration Release --no-build
Pop-Location
```

Check every native exit code before continuing. `git archive` uses committed
source; do not silently substitute a different release or unreviewed local
code. The patch is context-checked, not commit-gated. Run dotnet inside the
patched copy so its SDK 10 `global.json` is selected; invoking an SDK 10 host
from the unpatched SDK 9 project directory does not override `global.json`.

The preserved temporary build passed `31/31` tests and offline type/JIT checks.
Original candidate DLL SHA-256:
`774b87c811a932b3da81a76ce692b79899ea77a1b4559b1b2bb83a2c97e917a5`.
It and its evidence remain under
`%TEMP%/pr-zion-jf12-plugins-0439412ee02f431ba5be529e462c3512/` in
`artifacts/postprocessor/`, `results/postprocessor.trx`, and
`postprocessor-loadcheck.log`. Patches replay to the same build inputs after
CRLF/LF normalization. Path, line-ending, SDK, and source-revision labels can
change DLL bytes; hash each new build separately rather than claiming it is
the preserved binary. This is not a catalog release or live task acceptance.

The retained-patch replay matched all 44 shared source/build files and passed
`31/31` tests. Only the two ancillary packaging scripts were absent from the
original temporary source copy. The replay DLL hash is
`c6def1647fed54e7403b0c5d6563bb727b4aa234bed7e975563624c456133723`
under `%TEMP%/pr-zion-portable-postprocessor-6cc18bd25c574746a1d7c8de04124a03/`.
Both DLLs report file/product version `0.2.0.2`; source directories and line
endings differ. Source reproducibility is proven, byte reproducibility is not.

## Historical Deployment

Version `0.2.0.2` replaced the migration-era Python/systemd pipeline after
one-item, full controlled, and official scheduled-sync proofs on Jellyfin
`10.11.11`. Audit-only remains the installation default for new servers.

On 2026-09-08 CT108 ran the unpublished ABI-12 candidate identified above
on Jellyfin `12.0.0`. Isolated and production startup/registration passed;
original plugin configuration, checkpoint and audit files were preserved.
The existing watcher settings were restored after the required library scan.
New metadata-write acceptance was not exercised; historical 10.11 write proofs
must not be described as a new 12.0 task run. Its local plugin metadata disables
automatic updates until a compatible published replacement is selected.

This historical deployment does not establish acceptance for 0.3.0.0. Release
publication is not proof of a production metadata run.

## License

GPL-3.0.
