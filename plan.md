# Clip Manager complete UI/UX redesign plan

Updated: 2026-10-13. Baseline release: **2.1.12**.

This replaces the completed feature implementation plan. The next project is a complete redesign of the desktop experience: navigation, every operation, presets, settings, tray behavior, progress, errors, recovery, accessibility, and presentation. The goal is a useful, polished application that makes large clip libraries easy to manage and makes file operations understandable before they run.

**Status:** planning complete; redesign implementation has not started. The 2.1.12 version bump includes the previously completed Archive / Seek duplications feature work and this roadmap. It does not mean the redesigned interface is implemented or that production-drive acceptance is complete. Original feature notes remain in `todo.md`.

## 1. Outcomes and scope

The redesigned app must let a user:

1. See what is running, which folders are involved, and what needs attention immediately after opening it.
2. Configure an operation in one place, review its effects, run it, and inspect its result without a chain of prompts.
3. Distinguish archive copying, batch moves, renaming, duration tracking, and recycling without guessing from button colors.
4. Compare individual duplicate copies using their folder and batch context, preserving at least one verified copy.
5. Use presets, keyboard navigation, and tray controls without depending on hidden right-click commands.
6. Recover from unavailable drives, changed files, cancellation, and settings problems without losing track of completed work.
7. Use a consistent interface at different window sizes, text scales, themes, and display scales.

Scope includes all current actions and all user-facing windows. Keep the existing Windows x64, .NET 10 WinForms application and portable executable. Start with reusable native controls and a thin presentation layer; validate the hardest screens in a real WinForms prototype before committing the full layout. A framework migration would require a separate decision backed by a demonstrated limitation. No new cloud service, database, subscription, similarity matching, or video editor is needed for this redesign.

## 2. Current experience audit

These are code-observed design problems; user-reported bugs still need reproduction and a regression record.

| Surface | Current friction | Redesign outcome |
| --- | --- | --- |
| Main window | 520 x 400 button grid, absolute positioning, competing button colors and emoji | Resizable shell with clear navigation, folder context, operation content, and persistent status |
| Startup / tray | Window immediately hides; constructor opens setup and registers Windows startup | Visible, skippable first run; explicit startup and close behavior; predictable activation |
| Folder selection | Working folder, tracking roots, archive sources, and duplicate roots are spread across dialogs | Clearly labeled folder roles with shared pickers, validation, availability, and remembered choices |
| Archive | Separate utility window; checkbox implies run-once mode; status and errors are mostly concatenated text | Integrated workspace with explicit mode, source health, queue/phase status, and actionable issues |
| Duplicate review | Very wide grid of raw paths/bytes; legacy cleanup uses a read-only text list | Grouped review, readable metadata, folder context, selection summary, and exact final confirmation |
| Identify clip | Multiple pickers and result/action dialogs | Reference, search scope, results, destination, and action review in one workspace |
| Batch / naming tools | Repeated prompts; consequential moves/renames are hard to visualize | Structured configuration and an exact change preview with clear cancellation semantics |
| Presets | Important commands mainly in context menus; editor exposes raw enums and multiline source paths | Visible preset access inside each workflow, consistent editors, and human-readable summaries |
| Settings | Watch-folder setup is separate from other preferences; startup is implicit | One settings destination with distinct General, Tracking, Updates, and About sections |
| Feedback | Modal success/error boxes and brief generic completion labels | Persistent operation outcomes, useful empty states, recoverable inline errors, and Activity details |
| Visual consistency | Dark main window and default-looking secondary forms; scattered typography/layout | Shared theme, typography, icons, spacing, focus styles, and control behavior everywhere |

During the audit, inspect `Program.cs`, all `Form1*.cs` files, all designer-backed forms, `ArchiveForm`, `DuplicationReviewForm`, `PresetsForm`, `ProgressForm`, and `AppUpdater`. Record whether each screen is active, replaceable, or unused before removing anything.

Create `docs/ux/bug-audit.md` with reproduction steps, expected/actual behavior, severity, affected workflow, evidence, and regression coverage. Prioritize crashes, wrong file/context selection, incorrect completion claims, freezes, and lost recovery information before visual polish. Audit startup/activation, cancellation boundaries, owner/minimize behavior, late UI callbacks, drive loss, invalid settings, and errors during rollback. Do not present suspected bugs as verified defects.

## 3. Product structure and navigation

Use one primary window with a left navigation rail, workspace header, content area, and a compact persistent operation strip. Secondary dialogs are reserved for OS file picking, a consequential final confirmation, and focused editing where useful.

| Destination | Contents |
| --- | --- |
| Overview | Active work, archive health, working folder, duration tracking summary, recent outcomes, and useful next actions |
| Archive | Source/destination configuration, run mode, speed, current work, pending/retrying issues, and archive details |
| Batches | Prepare Batch and Shuffle Batches as clearly separate tools |
| Find & clean | Compare folders (Seek duplications), Clean one folder (Remove duplicates), and Identify clip |
| Rename & shuffle | Shuffle & Name, Name by 10s, and Shuffle Random |
| Activity | Current and recent jobs, phase progress, outcomes, warnings, errors, and recovery locations |
| Settings | General behavior, Duration tracking, Presets management, Updates, and About |

Retain existing action names in help/preset mappings where new navigation labels differ. Every current action must remain reachable from the window and its applicable tray/context entry.

The header shows the selected workflow and its relevant folder roles. A working-folder selector belongs to workflows that use it; it must not imply that all tools share that target. Changing the working folder never silently edits Archive sources or tracking roots. Show a compact global archive indicator that opens Archive even while a different tool is visible.

Suggested shell layout, expressed in logical pixels rather than fixed physical coordinates:

```text
+-----------------------------------------------------------------------+
| Clips Manager                     working folder / contextual actions |
+---------------+-------------------------------------------------------+
| Overview      | Workspace title                preset menu / help     |
| Archive       | Short explanation of what this tool changes           |
| Batches       |                                                       |
| Find & clean  | Configuration / results / review                       |
| Rename        |                                                       |
| Activity      |                                                       |
| Settings      | Primary action + concise selection summary            |
+---------------+-------------------------------------------------------+
| Archive: Watching, 3 pending | Current job: scanning | View activity   |
+-----------------------------------------------------------------------+
```

Default window target: approximately 1180 x 760 logical pixels. Support a compact 900 x 600 layout with a collapsed rail, stacked configuration panels, and a scrollable workspace; adapt further when the available work area is smaller. Save bounds, maximized state, selected destination, and useful table widths. Clamp restored bounds to a connected monitor and avoid restoring unusable DPI-scaled geometry.

## 4. Visual and interaction system

Deliver `docs/ux/design-system.md` and reusable controls before converting all screens.

- Style: calm, professional desktop tool. Neutral surfaces, one restrained accent, clear hierarchy, and readable file context. Replace multicolored emoji buttons with consistent icons and text labels; reserve red for consequential recycle/error states.
- Theme: System, Light, and Dark. Apply tokens to the shell, menus, dialogs, grids, tooltips, progress, and empty/error states. Respect Windows high contrast with system colors instead of forcing brand colors.
- Typography: Segoe UI, approximately 10-11 pt body, 16-20 pt page headings, and 12-13 pt section headings. Use readable metadata text without a tiny secondary-font dependency. Test actual Windows text scaling.
- Spacing: 4/8/12/16/24 logical-pixel scale; consistent field widths and button heights. Use docked/table/flow layouts and font/DPI scaling instead of control-by-control coordinate calculations.
- States: default, hover, pressed, focused, disabled, loading, invalid, selected, and needs attention. Disabled actions have a visible reason nearby. Never rely on color or hover-only help to communicate essential information.
- Files: show readable filenames and relative context first; make full paths available in details, tooltips, and Copy path. Final confirmations always expose exact full paths. Long text must not cover buttons or resize the whole window unpredictably.
- Units: durations as `1h 12m 08s`, sizes with an explicit unit, and localized timestamps with exact values in details. Archive's MB/s setting remains decimal aggregate application I/O; explain this in its help.
- Feedback: short labels and useful next steps. Empty folders, no matches, partial scans, permission errors, disconnected roots, cancelled work, and first use each get a distinct state.
- Motion: subtle optional transitions only; no animated decoration or permanent spinning while merely watching. Respect reduced-animation preferences.

Accessibility is part of every component: logical Tab order, visible focus, access keys, keyboard alternatives for menus, UI Automation names/roles, meaningful status announcements, and no destructive default Enter action. Escape cancels a picker/editor or returns from review; cancellation of running work uses its explicit control. Target at least 4.5:1 contrast for ordinary text and 3:1 for essential control/focus boundaries. Validate with keyboard-only use and Windows Narrator, including custom-drawn controls.

Starting color tokens for the native prototype (adjust only after contrast/state checks):

| Token | Light | Dark |
| --- | --- | --- |
| Application background | `#F4F6F8` | `#12161C` |
| Panel / input surface | `#FFFFFF` | `#1C222B` |
| Main text | `#17212F` | `#EDF2F7` |
| Secondary text | `#526174` | `#B0BECE` |
| Primary action with white text | `#245CC5` | `#245CC5` |
| Destructive action with white text | `#B42336` | `#B42336` |

Use dedicated contrasting focus/border tokens, not a low-contrast decorative divider as a control boundary. Success/warning colors supplement icons and text rather than coloring entire workspaces. High Contrast replaces these tokens with Windows system values.

## 5. Shared operation experience

Every tool follows **Configure -> Analyze/preview -> Review -> Run -> Result**, omitting an unnecessary stage for copy-only work. Navigating away keeps the job visible and does not launch another one. A preset fills the same configuration fields; it must not open a different, less understandable workflow.

Navigation preserves in-session drafts, results, and selections. Explicitly discarding edited settings/presets requires a focused decision; merely viewing another workspace does not. Persist useful field defaults separately from a running job's frozen configuration. Do not restore old destructive selections automatically after restarting the application.

Build shared folder/file pickers with editable paths, Browse, recent local paths, Open folder, Copy path, and inline validation. Support pasted paths and drag/drop where unambiguous; wrong item types explain the problem. Show unavailable roots without discarding saved choices. Validate permissions, source/destination containment, links, and collision risks before starting, then keep the backend's execution-time checks.

Shared review lists show operation type, original path, proposed path/action, conflicts, and estimated totals. Freeze the reviewed plan for execution. Random renaming/shuffling must use the same generated mapping the user reviewed; do not generate a different one after confirmation. Revalidate identities and destination availability immediately before changes, reporting changed items instead of silently selecting replacements.

Use explicit state transitions: Editing, Analyzing, Ready to review, Running, Cancelling, Completed, Completed with issues, Cancelled, and Failed. Archive also has Watching, Waiting for a stable file, Paused, Waiting for foreground work, and Suspended with a reason. The view renders service state; hiding a view never controls worker lifetime.

Use determinate progress only when a total is meaningful. Show the current phase (enumerating, hashing, copying, verifying, moving, recycling, or recovering), files/bytes where available, elapsed time, and cancellation state. Completion summaries distinguish changed, skipped, failed, untouched, and recovered items. A partial scan cannot say that no duplicates exist, and a run with failures cannot present a plain success label.

Foreground file mutation remains serialized through `OperationCoordinator`. Background archiving yields at safe boundaries, stays subscribed to watchers, and resumes afterward. Disable conflicting execution controls while leaving navigation, logs, and safe configuration viewing usable. Draft edits never modify an active run; show whether changes apply to the next run.

Results remain visible in the workspace and Activity. Use dialogs for a meaningful final decision, not for routine success. Recycle/move/rename confirmations summarize exact changes and recovery behavior, with a neutral Cancel as the safe default. Never promise undo for an operation that only supports partial completion.

## 6. Redesign every workflow

### 6.1 Overview and first use

- First launch shows the window with a skippable setup checklist: choose a working folder, optionally configure duration tracking, optionally configure Archive, and choose startup/tray preferences. Do not require Archive setup to use naming tools.
- Overview prioritizes current work and needs-attention items, then folder context and recent results. Each card has a clear destination and one useful primary action.
- Archive summary distinguishes verified copies, pending files, unavailable roots, and last success. A last-success time does not mean that the whole source library is protected.
- Folder summaries refresh asynchronously and expose last refresh/availability. Avoid a blocking full enumeration or content hash on the UI thread.

### 6.2 Archive

- Source list: Add folder, Remove, root label, availability, and inclusion explanation. Show overlaps as one effective scan scope; explain excluded links/junctions. Removing a source from configuration never deletes either source or archive files.
- Destination: editable picker, availability, containment validation, and optional asynchronously obtained capacity information. Distinguish unknown capacity from enough capacity; backend errors remain authoritative.
- Mode: explicit Run once / Watch continuously selection. Independently select Fast / Slow with short explanations. Slow defaults remain one clip per 120 seconds and optional 20 MB/s aggregate content I/O; settle time defaults to 10 seconds. Advanced controls expose units and validation without overwhelming the basic form.
- Primary controls reflect state: Start, Pause, Resume, Stop. Explain that Stop disables automatic watch resumption and retains completed copies; closing the window leaves enabled background work running.
- Status: current file, phase and bytes, pending count, copied-and-verified count, already-archived count, retry/failure count, and last successful verification. Show why an idle worker is waiting.
- Issues view: actionable root/file problems with last attempt, retry state, and full details. Retry uses existing deduplication/backoff/coordinator rules; it never creates a second worker. If per-file queue details are not available, add bounded immutable status projections to `ArchiveWorker` rather than parsing its log text or exposing its mutable queue.
- Archive details: original name/root, current archive location, verification information, and Open/Copy path. Cached identity or a previous success must not be presented as a fresh whole-library integrity check. Load index details asynchronously and page large collections.
- Destination disconnect/full disk becomes a persistent needs-attention banner with recovery instructions. No modal popup per file or automatic source deletion.

### 6.3 Compare folders / Seek duplications

- Configure A and B with clearly labeled paths, subfolder choices, overlap explanation, and saved presets. Keep A/B folder roles visible during review.
- Results are exact-identity groups, using the existing size, full SHA-256, and duration matching. No perceptual/similarity claim.
- Each group displays filename, folder/batch context, side, duration, readable size, and modified time. A details pane reveals full path and identity information without requiring ten wide columns.
- All copies start as Keep; neither side gets priority. Each copy has an explicit Keep/Recycle control. Prevent recycling every copy in a group, and keep physical hard-link aliases protected with an explanation.
- Sorting, filtering, collapsing groups, and opening a clip never change selections. Key selections by stable copy identity/path, not grid row number. A visible summary includes selections hidden by a filter.
- Offer Open clip, Open containing folder, and Copy path for a selected copy. External-player failures become recoverable errors. No embedded video playback dependency is required.
- Review selected copies only before executing; show exact paths, keeper context, count, and conservative reclaimable bytes. Revalidate keepers/targets at execution and report changed or missing files as skipped.
- Recycling uses the existing Recycle Bin implementation with no permanent-delete fallback. Cancellation retains completed recycling and clearly reports untouched copies.

### 6.4 Clean one folder / Remove duplicates

Keep this distinct from A/B comparison. Its existing alphabetical keeper policy must be stated before scanning and in the preview. Replace the read-only text wall with the shared group/context review. Show proposed keepers and extra copies, unreadable items, and exact recycle confirmation. Offer the A/B tool when the user needs cross-folder context instead of silently changing this action's policy.

Audit its execution adapter and failure/cancellation outcomes. Present Recycle terminology consistently; any permanent-delete fallback in a legacy path must be removed or blocked before it can be offered as safe recycling. Preserve verified matching and existing rollback behavior where applicable.

### 6.5 Identify clip

Put reference clip, search folder, recursive scope, Scan, results, and intended action in one workspace. Show the reference distinctly and exclude it from mutation. Results use the shared file-context list with Open/Copy actions. Allow inspecting results without choosing a mutation. Move matches requires a destination and collision preview; Recycle matches requires exact review. Keep source/reference verification and report unreadable paths separately from a complete zero-match result.

### 6.6 Prepare Batch

- Show source, destination parent, selection mode in plain language, target duration, and naming behavior in one form.
- Preview the resolved highest-numbered immediate `c_` starter folder, or the exact new batch folder. Show starter count/duration separately from selected incoming clips and the planned total.
- Explain source shuffling, duplicate recycling, backup location, and clip moves before confirmation. Show the proposed filenames and destination paths; starter filenames stay unchanged and occupied numbers are skipped.
- Keep automatic `c_` discovery, next available/custom-name collision rules, random source selection, supported selection scopes, and shortest-to-longest numbering.
- Introduce a shared read-only planning model if the current API cannot produce a preview without mutation. Analyze cannot shuffle/rename/create Backup as a side effect. Execution reuses the resolved destination and selection and revalidates them.
- Display the applicable recovery contract: presets have in-process transaction rollback; legacy manual flows can retain partial progress. Harmonizing these contracts requires an explicit backend change and regression coverage before the UI promises it.

### 6.7 Shuffle Batches

Show archive/root scope, target batch duration, included/excluded paths, and a before/after batch summary. Explain that clips are moved/renamed across the pool, links and Backup are excluded, and this action does not recycle duplicates. Preview final folder/file mappings, shortest-to-longest numbering, and over-limit clips receiving their own batches. Execution uses the reviewed plan and existing rollback rules; archive reconciliation must find moved archive copies afterward.

### 6.8 Rename & shuffle

Provide three clearly separated modes: Shuffle & Name, Name by 10s, and Shuffle Random. Use appropriate start number, padding, prefix, random-name length, and target folder fields. Explain that Name by 10s is a numbering step, not a duration filter.

Preview original -> proposed filenames with collision/error markers. Refresh preview is explicit for randomized operations; Run uses that preview's mapping. Show affected count and preserve extensions. A no-change or empty-folder result has a useful explanation. Document manual versus preset recovery behavior until a shared transactional execution path is implemented and tested.

### 6.9 Presets

- Keep action right-click menus and tray Manage presets access. Add a visible preset selector within each workspace and a manager under Settings; do not restore the crowded standalone main-window button that the earlier requirement removed.
- Show name, action, target resolution, and a concise description. Clearly distinguish Current working folder from a fixed target and show the currently resolved path before execution.
- Apply preset fills the normal form; Save preset, Edit, Duplicate, Rename, Delete, and Set default are explicit. Always ask / no default means opening configuration, not repeatedly asking separate questions.
- Redesign action-specific editors using shared controls. Archive sources use Add/Remove rows instead of newline text; labels translate raw enum names into understandable selections.
- Preserve existing JSON/numeric `PresetAction` mappings, legacy migration rules, and Archive's run-once semantics. Seek presets always retain review and confirmation. Existing default presets remain defaults, but default execution must expose folder context and required review rather than silently mutate files on a navigation click.
- Save settings atomically and surface failures. Invalid/unavailable paths remain editable, with warnings and execution-time checks; do not destroy a useful preset because a removable drive is temporarily absent.

### 6.10 Duration tracking and settings

General includes launch at Windows startup, launch into tray, close-to-tray behavior, theme, and notification preferences. Detect and display the existing startup registration; preserve a user's current choice while making new enablement opt-in. Changing it updates the registry safely and reports failures. Do not rewrite startup registration unconditionally on construction.

Duration tracking gets a root list with Add/Remove, availability, current totals, last refresh, and Refresh. Explain that duration marker files are written for each watched folder and its immediate subfolders and that tracking roots are independent of Archive sources. Removing a root stops tracking without implying clip deletion. Preserve existing marker cleanup rules, overlapping roots, legacy configuration migration, and operation pause/refresh coordination.

Keep Archive configuration in Archive, with a link from Settings. Add About with installed version and data/log location. Update checking has explicit checking, available, downloading, applying, failed, and current-version states; a manual check must not freeze the window or lose the active workspace. Block executable replacement/restart while unsafe foreground work is active and stop/drain background work safely before applying an update.

### 6.11 Activity, diagnostics, and recovery

Use a bounded local history of job summaries and expandable details: action, resolved folders, timestamps, phase, counts, outcome, errors, and recovery paths. Keep full file inventories out of unbounded in-memory logs. Separate running tasks from archive watch state and completed history. Provide filters and Copy details/Open log location for support; do not add automatic telemetry.

A failed transaction shows what was restored, what could not be restored, and where retained recovery files are located. A cancelled archive run shows verified copies retained and incomplete work pending. Recycle results show completed versus untouched paths and explain manual Recycle Bin recovery; do not offer a fake one-click undo. Persist summaries needed after restart without storing misleading in-progress success states.

### 6.12 Tray, activation, shutdown, and notifications

- Left click activates the existing window predictably; second instance signals that window instead of claiming a refresh without doing one. Add explicit single-instance activation IPC and test failure/fallback behavior.
- Menu: Open Clips Manager, Archive status, applicable Pause/Resume/Stop, common tools, Manage presets, Duration tracking, Settings, Check for updates, and Exit. Show checked/disabled state and keep labels consistent with the window.
- Tooltip fits Windows limits and summarizes tracking/archiving accurately. Notifications are optional and limited to meaningful completion or problems requiring attention; no per-file spam.
- Closing to tray is explained once and follows the saved setting. Hiding/closing a workspace cannot recreate workers or reset progress.
- Exit cancels active foreground work, awaits its recovery where supported, stops workers/watchers, persists settings/history, and disposes timers/icons. Keep the UI responsive during shutdown and show why it is waiting; never claim rollback is complete before it finishes. Windows logoff/shutdown follows a bounded safe path without assuming it can finish long work.

## 7. Implementation architecture and data compatibility

Keep file-operation services authoritative. Extract presentation code from `Form1` incrementally, avoiding a simultaneous rewrite of tested hashing/copying/recycling algorithms.

| Layer / component | Responsibility |
| --- | --- |
| `MainShell` and workspace views | Layout, navigation, focus, view lifecycle, and rendering |
| Small presenters / view state models | Validation, commands, immutable displayed state, review selection, and job transitions |
| `JobController` / status adapter | Wrap existing progress and `OperationCoordinator`; serialize mutation, surface structured outcomes, and cancel safely |
| Shared controls | `PathPicker`, `SourceFolderList`, `PresetSelector`, `StatusBanner`, `OperationStrip`, `FileContextList`, `ChangePreview`, and empty/error states |
| Theme resources | Central colors, fonts, spacing, icons, state styles, and DPI handling |
| Existing domain services | Archive worker/index/content, duplicate identification/recycling, batch planning/execution, transactions, tracking, and updater |
| UI settings/history storage | Versioned, atomic local persistence independent of archive identity/index schemas |

Use lightweight presenters rather than adding an application framework by default. Add structured operation results where existing human-readable summary strings are insufficient; retain compatibility adapters during migration. Future preview APIs must return immutable plans and must not perform mutations during analysis.

All enumeration, hashing, metadata reads, index loading, and heavy result preparation run away from the UI thread. Marshal view updates safely, cancel obsolete loads, and reject callbacks after disposal. Coalesce frequent progress into the latest snapshot at approximately 250-500 ms intervals; do not enqueue one UI callback per copied chunk or watcher event. Use virtualized/paged tables for large results and preserve selection independently of row materialization.

Keep existing settings/index files and numeric preset identities readable. Store new preferences separately (for example `ui_settings.json`) with schema version, defaults, atomic write, and recoverable corruption handling. Never reset archive-watch enablement or drop unavailable roots during UI migration. Test old configurations before removing old forms. Dispose subscriptions/timers per view, while the application owns worker lifetime.

## 8. Non-negotiable safety invariants

- Archive always copies and verifies; source files and existing archive filenames remain untouched by archiving. Completed copies survive cancellation.
- Full content verification, protected source handles, read-back verification, no-overwrite publication, atomic indexing, and crash reconciliation remain intact. Metadata cache hints are not proof of identical content.
- Preserve recursive overlap deduplication, link/junction exclusion, source/destination containment checks, settling, I/O limiting, queue bounds, backoff, and unavailable-drive handling.
- Foreground priority must not leave a worker permanently paused or launch duplicate copies. Stop explicitly disables watch auto-resume; Pause and hide do not.
- A/B duplicate review starts with Keep everywhere, protects a keeper per group, respects physical aliases, and verifies both selected copies and keepers again before recycling.
- Recycle actions have no permanent-delete fallback. A preview never serves as permission to recycle a different file that later appears at the same path.
- Keep batch/rename collision and rollback protections, and expose manual versus preset differences accurately. Never equate cancellation with no changes unless that is the actual outcome.
- Preserve duration-marker ownership rules and tracking coordination. UI refreshes must not create a second tracker/worker or alter clip contents.
- Do not release while results, errors, or recovery files are hidden behind a success label.

## 9. Execution roadmap and deliverables

Each phase has a reviewable artifact and an exit gate. All implementation boxes remain open; the completed earlier feature work is the baseline, not completion of these phases.

### Phase 0 - Audit and baseline

- [ ] Inventory every screen, entry point, command, setting, and tray action; map them to the new destinations.
- [ ] Capture current screenshots and measure startup, scan, UI responsiveness, memory, and large-result behavior on disposable fixtures.
- [ ] Create the prioritized bug audit with reproducible failures and coverage expectations.
- [ ] Document current safety and cancellation contracts for every manual/preset workflow.

Exit: no current action is omitted; confirmed safety/correctness bugs are prioritized; baseline data and fixture steps are reproducible.

### Phase 1 - Design and native prototype

- [ ] Write design tokens, component states, terminology, and accessibility conventions.
- [ ] Create annotated wireframes for every destination and its empty/loading/error/result states in `docs/ux/wireframes.md`.
- [ ] Build a disposable native prototype of the shell, Archive, and grouped duplicate review with generated data.
- [ ] Verify resizing, mixed DPI, long paths, keyboard use, and large review lists before converting production screens.

Exit: validated implementation direction is concrete, all major states are specified, and native WinForms can meet the layout/accessibility requirements.

### Phase 2 - Shell, state, and common components

- [ ] Introduce the shell, navigation, theme resources, shared pickers, presets access, status strip, and structured job presentation.
- [ ] Add UI settings persistence, restored/clamped bounds, and skippable first-run setup.
- [ ] Resolve startup, activation, close-to-tray, subscription/disposal, and conflicting-command defects identified in the audit.
- [ ] Temporarily route unfinished workflows to their existing forms so the app stays usable during migration.

Exit: every action still works; navigation does not affect worker lifetime; first-run and tray behavior are predictable; common controls pass accessibility checks.

### Phase 3 - Archive and file-context review

- [ ] Replace Archive configuration/status with the integrated workspace and structured issue projections.
- [ ] Build the shared virtualized file-context/group review components and stable selection model.
- [ ] Convert Seek duplications, Remove duplicates, and Identify clip, including partial-scan/error states and exact confirmations.
- [ ] Verify foreground/background handoff, availability/retry controls, all-Keep defaults, aliases, and changed-file guards.

Exit: background status is always discoverable; all cleanup decisions show folder context; hidden selections and partial errors cannot produce misleading actions.

### Phase 4 - Batch and rename workflows

- [ ] Add read-only planning APIs where needed; share plans between previews and execution.
- [ ] Convert Prepare Batch and Shuffle Batches with resolved destinations and before/after summaries.
- [ ] Convert the three naming/shuffling modes with collision validation and fixed reviewed random mappings.
- [ ] Test cancellation, rollback, changed destinations, starter preservation, and archive reconciliation after moves.

Exit: every file-changing action has a meaningful review and accurate recovery contract; previews are mutation-free and execution matches them.

### Phase 5 - Presets, tracking, settings, Activity, and updates

- [ ] Replace preset management/editor surfaces, preserving stored action mappings, defaults, and legacy migrations.
- [ ] Integrate duration tracking, General/Updates/About, and startup/notification preferences.
- [ ] Add bounded Activity persistence, recovery details, and diagnostics access.
- [ ] Complete tray parity, single-instance activation, safe update restart, and responsive shutdown.

Exit: all remaining surfaces use the same components and language; old settings upgrade without data loss; recovery remains discoverable after restart.

### Phase 6 - Accessibility, performance, acceptance, and rollout

- [ ] Complete the visual/accessibility/performance matrix below and fix all blocking defects.
- [ ] Run existing domain integration checks plus meaningful new workflow/lifecycle/migration tests.
- [ ] Validate a production-sized video library and real removable/full-drive behavior using backed-up data; record what was simulated versus physically tested.
- [ ] Remove superseded forms/dialog helpers only after confirming that no callers remain; update linked test sources, README, screenshots, help, and release notes.
- [ ] Release the redesign with its own subsequent version bump, build/publish validation, and an upgrade/recovery checklist.

Exit: all acceptance criteria are met or a specific non-blocking limitation is documented; no unresolved data-safety defect; all current workflows remain available.

## 10. Acceptance criteria and verification

### Visual and accessibility

- All destinations, editors, confirmations, tray menus, and update/error screens share the design system.
- Test Light, Dark, System, and High Contrast; 100%, 125%, 150%, and 200% DPI; Windows text scaling; mixed-DPI monitor movement; compact and maximized windows. Use a pairwise matrix plus the worst combined compact/high-scale case.
- No clipped primary action, overlapped field, invisible validation, or unreachable review control. Horizontal scrolling is limited to genuinely wide data; selected-file details and exact paths remain accessible.
- Every primary workflow completes with keyboard only. Narrator identifies fields/actions/selection and meaningful operation changes without repetitive per-file announcements.
- Capture and inspect screenshots for Overview, Archive in all key states, duplicate review/confirmation, batch preview, rename preview, presets, settings, and Activity. Automated checks do not replace visual inspection.

### Workflow and safety

- Opening any tool exposes its folder context and configuration; no action depends solely on right-click discovery or starts a destructive operation merely because navigation was selected.
- A valid saved configuration/preset can be reviewed and started from its workspace without repeated folder pickers. Changing a draft while another task runs cannot change that task's resolved paths.
- Renaming, moving, and recycling show exact intended effects; generated previews match execution. Filter/sort/refresh preserve selections and the final count includes hidden selections.
- Test no files, no matches, partial scan, invalid path, unavailable source/destination, permission denial, changed/locked file, collision, cancellation during each phase, rollback failure, and app restart.
- Verify close/hide/reopen, second-instance activation, startup preference, watch auto-resume, Stop persistence, foreground handoff, update interruption, and Exit during copy/recycle/recovery.
- Upgrade fixtures cover old working/tracking settings, all numeric preset actions, Archive presets, A/B presets, corrupt UI preferences, and disconnected roots without losing saved values.

### Performance and responsiveness

- Navigation, focus, scrolling, Cancel, and tray activation stay responsive while scanning/hashing/copying. Target visible acknowledgement within 100 ms under generated-load tests; measure on a documented machine rather than promising physical disk completion in that time.
- Use at least 10,000 result rows and 50,000 synthetic catalog entries to verify virtualized/paged rendering, stable selections, and bounded memory. Measure first-screen availability and subsequent interaction separately from completion of the scan.
- A watcher/event burst must retain the existing bounded queue and recovery behavior; UI work and activity history must also remain bounded. Do not replace existing service limits with unbounded presentation lists.
- Repeatedly navigate/open/close workspaces during an active job: no extra workers, stale disposed-control callbacks, leaked timers/subscriptions, or accumulating UI event queues.
- Confirm cancellation is acknowledged promptly and stops at the next safe service boundary. Report a real waiting/rollback state while slow OS I/O completes instead of freezing or promising a hard completion time.

### Build and regression gates

```powershell
dotnet build -c Release
dotnet run --project VideoTrayApp.Tests -c Release
dotnet publish VideoTrayApp/VideoTrayApp.csproj -c Release
```

The completed feature baseline has 210 passing generated-clip integration checks, including archive identity/publication/crash recovery, watcher priority/lifecycle, duplicate protection/recycling, presets, batches, and tracking. Keep those scenarios working as presentation code changes. Add tests for substantive new contracts (preview/execution identity, stable filtered selection, activation, settings migration, disposal, and structured partial outcomes), not tests that simply restate colors or control coordinates.

Production-library throughput, real drive removal, and an actually full destination were not completed in the earlier feature acceptance. Carry them forward explicitly; simulated failures do not replace those manual checks. Run destructive acceptance scenarios only on disposable/backed-up fixtures.

## 11. Definition of done and release policy

The redesign is done when all nine current actions, tracking, presets, tray behavior, updates, settings, and diagnostics use the new experience; documented bugs are resolved; accessibility and performance targets are checked; existing safety guarantees still hold; and a new user can configure, review, run, and recover without external instructions.

For this request, bump **2.1.11 -> 2.1.12** in project Version/AssemblyVersion/FileVersion and README, verify the completed baseline, commit the prior feature work plus this replacement plan, and push to the current `main` branch. The existing workflow automatically builds/releases on a main-branch version bump. Do not describe the new roadmap as delivered UI or mark its implementation checkboxes complete. Future redesign releases receive subsequent version bumps after their own acceptance gates.
