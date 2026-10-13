# Redesign audit

Audit date: 2026-10-13. Baseline: 2.1.12, commit cd8e11e. Fixtures are disposable generated AVI clips. Findings below distinguish observed behavior from risks requiring manual reproduction.

## Screen and entry-point inventory

| Baseline surface | Status and replacement | Destination / entry points |
| --- | --- | --- |
| Program / Form1 / Form1.Designer | MainShell is now the production entry point; baseline Form1 remains a compatibility test fixture | Overview and seven navigation destinations; predictable tray activation |
| Form1.Archive / ArchiveForm | Replaced in production by ArchiveWorkspace; compatibility tests still exercise the original adapter | Archive; global indicator; tray status, Pause, Resume, Stop |
| Form1.SeekDuplications / DuplicationReviewForm | Replaced in production by WorkflowWorkspace + FileContextList | Find & clean / Seek duplications; tray; presets |
| Form1.Duplicates / text review helper | Replaced in production; alphabetical keeper policy retained | Find & clean / Remove duplicates |
| Form1.IdentifyClip / action helper | Replaced in production; source/reference roles and inspection are explicit | Find & clean / Identify clip |
| Form1.ShuffleBatches | Replaced in production by read-only planning and execution of reviewed mappings | Batches / Shuffle Batches |
| Form1 naming and Prepare Batch handlers | Replaced in production by RenamePlan and PrepareBatchPlan | Rename & shuffle; Batches / Prepare Batch |
| Form1.Presets / PresetsForm / PresetEditorForm | Manager hosted inside Settings; focused editor retained, using shared path/source controls | Visible workspace preset selectors; Settings / Presets; tray |
| SettingsForm and designer | Baseline watch-root editor retained for compatibility checks | Production Settings / Duration tracking |
| ProgressForm and designer | No production callers; retained pending removal of compatibility surfaces | Production JobController + persistent operation strip |
| AppUpdater | Domain download/apply adapter retained; production orchestration moved into Settings | Settings / Updates; tray Check for updates |

All nine numeric PresetAction identities are unchanged. Working folder, tracking roots, Archive sources, Archive watch enablement, settings/index files, and preset defaults remain independent. Existing default presets fill the new workspaces; opening a destination cannot authorize mutation.

## Prioritized findings

| ID / severity | Reproduction and evidence | Expected / actual baseline | Resolution and regression |
| --- | --- | --- | --- |
| UX-01 / high | Launch baseline Form1 with a fresh isolated data folder. A modal SettingsForm blocks construction; without a setup answer it does not finish. Observed while constructing the disposable UI fixture. | Visible skippable setup / mandatory modal tracking setup and tray hiding. | MainShell opens Overview visibly, with optional setup. UxChecks first-launch check. |
| UX-02 / medium | Read Program.ShowNotification and launch a second baseline process: notification claims duration refresh; no refresh/activation IPC exists. Code-confirmed misleading claim; second-process baseline behavior not manually exercised. | Activate existing window / balloon and 3.5-second sleep. | Named event activation replaces notification. UxChecks signals the event and verifies existing-window activation. |
| UX-03 / high | Inspect Form1.RunWithProgressAsync: returned partial summaries are discarded and label becomes Done. Existing domain fixtures deliberately produce changed/unreadable files and partial outcomes. | Persistent accurate result / generic Done with optional modal summary. | JobController persists explicit outcomes and details. Strict new cleanup adapters block incomplete scans and use transactions. Seek uses structured recycled/untouched/error counts. |
| UX-04 / high | Inspect OperationTransaction.Recycle / Commit: VB shell adapter requests recycling but does not enforce the dedicated RecycleBin-only API. Permanent deletion on unavailable recycling was a risk, not reproduced. | No permanent-delete fallback / weaker legacy adapter. | Both paths now call RecycleBin.Recycle. Existing real and injected recycle-failure checks retained. |
| UX-05 / high | Review Copy's former failure cleanup: target absence is checked before File.Copy; a collision racing that call could cause deletion of a foreign file. Code-observed race, not a reproduced production incident. | Never delete another writer's destination / absence snapshot was used as ownership proof. | Transactional copies use CreateNew; only outputs whose physical identity is owned can be cleaned. Recovery ownership follows moves, including cross-volume identity changes. |
| UX-06 / high | UxChecks: close MainShell through its draining Exit path, then dispose its using declaration. Initial redesign threw ObjectDisposedException on lifetime cancellation. | Repeated disposal harmless / disposed cancellation source reused. | Idempotent resource disposal; UxChecks exercises Exit followed by Dispose. |
| UX-07 / medium | Initial redesigned screenshots placed Analyze and Start below the initial viewport. | Execution controls visible at compact sizes / configuration pushed controls below scroll. | Tool Configure/Review switchers and pinned command rows; Archive pinned lifecycle controls. Screenshots inspected. |
| UX-08 / medium | Initial navigation measurements: Archive 1,061 ms, tool sections 528–849 ms. | Responsive acknowledgement / repeated whole-tree styling and layout. | Suspend layout during theme changes; theme new pages once; paint navigation title immediately. Warm navigation under progress burst has a 100 ms gate. Cold page construction is measured separately. |
| UX-09 / high | The native prototype harness showed empty reviews even after successful analysis; illegal-thread checks identified continuations modifying controls outside the UI thread. | Populated native review / background control updates. | Explicit UI-thread marshaling before async presentation changes; all eight foreground analyses now assert visible planned rows. |
| UX-10 / medium | The first screen-capture harness could capture an overlapping foreground application. | App-only evidence / unrelated desktop pixels. | PrintWindow captures the app HWND directly; regenerated captures inspected. |
| UX-11 / high | Batch destination gained a noncolliding starter after review. | Reviewed duration and selection remain valid / new arrival could silently change the batch scope. | Freeze and compare starter video inventory before any mutation; generated rejection check. |
| UX-12 / high | Disposable transaction fixtures replace owned staging/recovery paths externally. | Preserve external content / cleanup must not use path alone as ownership proof. | Physical ownership guards on rollback and committed cleanup; exact retained paths surface as warnings. |

## Cancellation and recovery contracts

| Workflow | Retained contract / production presentation |
| --- | --- |
| Archive | Copies and verifies only; completed copies retained, owned incomplete files cleaned, pending work rediscovered. Pause keeps watching; Stop persists watch disabled. |
| Seek duplications | All Keep initially; hard links stay Keep; one verified protected keeper per group. Cancel retains successful earlier recycling and reports untouched copies. Recovery is Windows Recycle Bin. |
| Remove duplicates | Alphabetical keeper remains authoritative. New workspace blocks partial-scan mutation and stages reviewed removals in an in-process transaction. Legacy manual adapter remains partial-progress behavior. |
| Identify clip | Inspect-only mode available. Reference and physical aliases excluded. New workspace freezes suffix destinations, verifies files/reference, uses an in-process transaction, and blocks incomplete scans. Legacy manual adapter remains partial-progress behavior. |
| Prepare Batch | New analysis simulates shuffled selection, duplicate cleanup/refill and exact backup/move destinations without mutation. New workspace executes that fixed plan transactionally; starter filenames retained. Legacy manual adapter remains partial progress. |
| Shuffle Batches | Analyze pools and packs once; execution uses those mappings. Both old Run adapter and new workspace use existing rollback. Physical replacement and metadata changes invalidate execution. No content hashing/copying introduced for this action. |
| Three naming tools | New read-only random/name mappings are frozen; full content/physical identity is revalidated; staging and publication are transactional. Legacy manual adapter retains its original partial-progress behavior. |

Forced termination/power loss remain outside in-process transaction rollback. Activity restores unfinished records as Interrupted and makes recovery context discoverable; it does not invent an undo action.

## Remaining manual reproduction / acceptance

Drive removal, an actually full destination, Windows Narrator, mixed physical DPI monitors, Windows text scaling, update executable replacement, and Windows logoff remain manual acceptance items. Existing integration tests simulate unavailable/full drives and rollback failures. Simulations are not physical-drive acceptance. Baseline designer forms remain compiled because compatibility checks still reference them; no unused form was deleted without addressing its callers.
