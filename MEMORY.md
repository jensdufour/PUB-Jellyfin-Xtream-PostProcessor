# Xtream Post Processor Decisions

- 0.5.0.0 source: before-flow native relationship reconciliation and source/group
  baseline; after merges require persisted reciprocity and source membership before
  search. Requires12.1 IncludeAlternateVersions; never rely on presentation grouping
  alone to include hidden editions. Uses native repository/persistence/link APIs,
  not SQL or a server patch. Upsert alone does not refresh cache: retrieve/register.
- Native local ownership and nested/multiple references within the same group are
  legitimate. Correct owned primary pointers only to an already-linked local owner;
  never repeat broad OwnerId clearing. Root self-link removal retains all other
  arrays to avoid native orphan-local deletion. Conflicts/locks fail closed.
- Sanitized full268806-record production fixture passed117 missing-link additions,
  five owner-preserving primary corrections and five root self-link removals;
  all2984OwnerIds retained. This is planner proof, not yet live repair proof.
  Fixture stays private/outside Git; optional test uses XTREAM_VERSION_FIXTURE.
- Stable providerFingerprint excludes DateLastMediaAdded. Canonical series check
  child labels locally every run. Legacy full-hash matches migrate without network;
  mismatches revalidate once. Child-only failure retains provider decision and retries
  locally. Cached local checks do not validate parent NFO; exact-provider writes do.
- 76 Release tests pass. Production0.5 activation remains pending. Incremental scans
  are deferred; reconciliation happens after a scan, not within its mutation path.

- 0.4.0.0 adds opt-in native follow-up sequencing to the existing watcher, not a
  separate daemon. Native task order: Normalize, MergeMovies, MergeEpisodes,
  full Meilisearch index. Xtream remains the sync/scan scheduler.
- September18 live0.4.0 repository installation, activation and configuration
  wakeup passed on Jellyfin12.1. Actual task times prove title -> movie -> episode
  -> search ordering; the checkpoint reached completed/4. No new sync/scan was
  launched. Backend queue subsequently drained; users/history/settings preserved.
 62 source tests and settings load/save check passed. Details and recovery artifacts
  belong to the deployment runbook, not this portable plugin repository.
- Require Merge Versions>=12.0.1 (awaited native writes), unrestricted title write
  configuration and empty downstream triggers. Never edit users' schedules from
  plugin code; remove conflicting timers as a backed-up deployment step.
- Atomic library-flow.json checkpoints bind sync+scan and current task timestamp.
  Same-cycle completed/failed states do not replay; uncertain interrupted runs stop.
  No global transaction against external/manual writers; recheck guards at handoffs.
  Search backend queue draining is not implied by its native task completion.
- 0.3.0.0 production title/NFO writes, preserving history/identity/media, were
  verified September17-18. Earlier unreleased/deployment-pending notes below are
  historical; current0.4.0 activation and native flow are verified as recorded above.

- Source 0.3.0.0 is an unreleased Jellyfin 12/.NET 10 candidate. API packages
  12.0.0 were available; 12.1.0 was unavailable on the configured feed. Local
  compilation and interface-double tests do not prove live 12.1 write behavior.
- On 2026-09-16 the user selected direct production implementation after the
  current scan, without a separate testing/rehearsal environment. The 0.3.0.0
  package is prepared in ignored dist/0.3.0.0; no installation or restart yet.
- Titles come only from the enabled TheMovieDb provider using the existing exact
  TMDb ID. Provider formatting and local OriginalTitle are not canonical sources.
  Deliberate custom titles must be locked before enabling writes.
- Native metadata savers can swallow failures. Read the enabled NFO saver's
  title back before accepting a write. NFO and database saves are not atomic;
  failure leaves the item retryable. Do not introduce a second NFO writer.
- A successful full scan starting after the last potentially changing sync is
  the indexing proof; an overlapping scan is insufficient. Explicit unchanged-sync
  counters can preserve an earlier proof within retained history. File-monitor
  refreshes alone and missing counters cannot prove scan completion.
- Task completion is a wakeup, not a cross-plugin transaction. This plugin owns
  only its in-process semaphore; async Merge Versions work or external writers
  may outlive task state. Keep them separate during guarded acceptance.
- Parent and child writes recheck sync/scan readiness and saved plugin settings
  immediately before mutation. Saving changed configuration cancels the old run
  before its next write, without discarding completed checkpoints. Saves already
  issued to Jellyfin may finish; cancellation is not a database rollback.
- Title state is separate from legacy overview state. Audit never checkpoints.
  Successful fingerprints skip unchanged titles; a fresh series media timestamp
  reopens child-label repair. Disabled/locked child metadata stays untouched.
- Keep published manifests and the historical compatibility patch unchanged.
  Next deployment gate: successful current scan, idle native metadata writers,
  existing database/config/NFO recovery coverage, then native plugin installation
  and the initial canonical task. Use the owning README's post-scan procedure;
  no additional operational script or isolated pilot is required. Observe actual
  production task results after implementation rather than claiming them now.