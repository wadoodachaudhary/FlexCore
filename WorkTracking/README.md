# Local work tracking

`LocalWorkBoardStore` persists a Feature → Epic → Story → Task tree in a host-selected SQLite database and private JSON folders. `WorkBoardControl` uses the existing Kanban control and shared inputs to show a searchable hierarchy and task lanes. Hosts supply source analysis, permissions, worker execution and the meaning of acceptance evidence; no application-specific names or behavior belong here.

Construct the store with `LocalWorkBoardOptions(privateDirectory)` and register it as a singleton per storage root. SQLite transactions also serialize independent store instances/processes. `CreateAsync` is idempotent for the same project key, source fingerprint, location and plan version; it refuses changed-source replacement. Plans begin in Draft. `ApproveAsync` makes tasks claimable after dependencies finish. `AssignAsync`, `ClaimNextAsync`, `RenewAsync`, `SubmitAsync` and `ReviewAsync` define the worker lifecycle. Leases expire after thirty minutes; expired results are rejected. Submitted evidence requires an independent reviewer identity before a task becomes Done. Parents become Done only when all children are Done.

SQLite is authoritative. `ExportAsync` regenerates project and individual ticket JSON files after an interrupted or failed projection. Projection failures are reported, not hidden; a database mutation may already have committed when such an error is returned. Consumers must read the current board before retrying a mutation. The activity journal is available through `EventsAsync`.

The store is not an authentication boundary and does not execute code or verify evidence. A trusted local CLI can use operator names; a hosted application must authenticate actors and authorize access to each project before invoking these methods. Keep the storage root out of public static-file directories. Host-level source checks and worker limits are also required before executing migration work.

## Planning and sprints

Optional `WorkPlanningDraft` checkpoints retain intake, source baseline, reviewed plan, selected model and an optimistic revision. Creating a board from one rechecks that revision and approval in the database transaction. Sprint membership and dependency order are validated; the board control can filter by sprint. Existing boards without these fields remain compatible.

Model assignments can target one task or unstarted descendant tasks. Hosts can claim a specific task and release a failed/cancelled run. `SubmitProposalAsync` records a proposal awaiting review, not verified work; `ReviewAsync` refuses to mark a proposal Done. The host must separately apply and verify implementation before normal result submission. Model calls, authentication, file access and execution are host responsibilities. No provider credentials belong in these records.
