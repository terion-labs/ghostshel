# ADR 0044: Provider-neutral complete file discovery

- Status: Accepted
- Date: 2026-08-15
- Extends:
  [ADR 0008](0008-file-provider-contract-and-local-semantics.md),
  [ADR 0009](0009-s3-and-webdav-file-provider-adapters.md),
  [ADR 0012](0012-durable-file-provider-runtime.md)

## Context

The File Viewer requested one page of 250 provider entries and presented a
manual **More** action. Sorting and name filtering happened only after that
page reached the view model. Consequently, a newly modified file could be
absent from a modified-descending view merely because the provider's native
enumeration placed it on a later page. The search field also searched only the
materialized rows.

The viewer supports local files, S3, SFTP, FTP, SMB, WebDAV, and embedded
Docker resources. Presentation code cannot solve discovery by calling a local
filesystem API without giving the protocols different semantics. Remote
protocols also do not share a push-notification facility.

## Decision

`IFilePanelClient` is the common discovery boundary. It exposes:

- `SearchAsync`, which walks provider listings and streams matching names;
- `WatchAsync`, which observes comparable provider snapshots and streams
  invalidations.

The default implementations use `ListAsync`, so every protocol with listing
support has the same search and observation behavior. A protocol may replace
the default observation with native notifications later without changing the
viewer.

The File Viewer automatically consumes continuation pages until the provider
reports completion. Page size remains a transport and memory bound, not a
user-visible result limit. Details and compact-list layouts virtualize their
rows, and the obsolete manual **More** action is removed. Sorting applies to
the complete materialized directory. Search is debounced, recursive below the
current location, cancellable, and independent of the displayed rows.

Observation polls the current directory through the provider boundary. The
first synchronized snapshot triggers one re-read to close the race between the
initial listing and observation startup. Subsequent snapshot changes refresh
the listing and repeat an active search. Remote transports therefore gain
correct automatic updates without protocol checks in presentation code.

Local and remote hierarchical providers retain bounded directory snapshots
behind continuation tokens. Later pages reuse that snapshot rather than
re-enumerating and re-sorting the directory. S3 continues to use its native
continuation token, and WebDAV continues to page its bounded PROPFIND snapshot.

Provider safety bounds on one response or retained snapshot remain in force.
They defend the process from hostile or unbounded remote responses; they are
not the former 250-row presentation limit.

## Consequences

- Every entry reachable through provider continuation is presented without a
  manual paging action.
- Sort order no longer changes which entries are eligible to appear.
- Search can find nested entries that were never rows in the current view.
- Automatic refresh works uniformly across all file-provider families, with
  polling cost determined by the configured interval and directory size.
- Recursive search can issue many remote listing calls and must remain
  cancellable and debounced.
- Snapshot-backed continuation consumes bounded provider memory in exchange
  for stable, linear pagination.
