## 2026-11-26 Read-only planning context

- Optional `WorkApplicationContext` records on planning drafts retain already-converted dependency names, folders, project entry points, reuse notes and host-produced metadata fingerprints. Existing drafts default to no context.
- Hosts own consent, safe inventory and freshness checks; the store only persists these values. Context does not authorize writes, execution or conversion of the dependency. Mirrored one-way from FlexKit.

## 2026-11-26 Phase-one planning and agent launchpad

- WorkTracking now persists revisioned planning checkpoints, ordered sprints, source-obligation references and model-agent assignments. Board publication checks the approved checkpoint inside the SQLite transaction.
- Specific-ticket claims and retry release support host launchpads. Model-authored proposals remain distinct from verified implementation and cannot be accepted as Done. Bulk assignment affects only unstarted child tasks.
- The shared board has sprint filtering. Hosts remain responsible for model consent, source access, authorization and isolated execution; the library runs no models or commands. Mirrored one-way from FlexKit.

## 2026-11-26 Optional local work boards

- Added the host-independent WorkTracking store and board control. SQLite is authoritative; private JSON folders are recoverable projections. Plans are hierarchical drafts with evidence, dependencies and acceptance criteria.
- Explicit approval, transaction-serialized claims, thirty-minute renewable leases, separate result submission/review, retry and parent roll-up are available to host orchestration. This does not run workers or authenticate operator identities; hosts own those boundaries.
- Existing applications do not register or open this store automatically. See WorkTracking/README.md. The source is mirrored one-way from FlexKit.
