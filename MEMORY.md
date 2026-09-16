# Xtream Post Processor Decisions

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