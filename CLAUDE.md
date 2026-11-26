## 2026-11-26 Optional local work boards

- Added the host-independent WorkTracking store and board control. SQLite is authoritative; private JSON folders are recoverable projections. Plans are hierarchical drafts with evidence, dependencies and acceptance criteria.
- Explicit approval, transaction-serialized claims, thirty-minute renewable leases, separate result submission/review, retry and parent roll-up are available to host orchestration. This does not run workers or authenticate operator identities; hosts own those boundaries.
- Existing applications do not register or open this store automatically. See WorkTracking/README.md. The source is mirrored one-way from FlexKit.
