# 2.2.4 Preserve original archive filenames

Archive now offers **Preserve original filename** below the destination. Each route and Archive preset saves this preference independently. When enabled, new verified copies retain the source filename and extension; occupied names receive `__1`, `__2`, etc. Existing archived copies retain their names, and identical content is still reused. Leaving the option unchecked preserves content-hash naming.

The Release build and full generated integration suite passed, including native checkbox behavior, route switching and restart, preset persistence, filename conflicts, Unicode names, duplicate detection and publication conflicts during verification.

# 2.2.3 Independent archive routes

Archive sources include only MP4 files directly inside each selected folder. Add a subfolder separately to scan or watch its files. Existing archive destinations are still indexed recursively so renamed and shuffled copies can be reused.

Named archive routes can watch different sources and destinations together, with separate settings, pending files, pause/stop controls, restart preferences, logs and catalogs. Existing single-archive settings migrate automatically. Slow scheduling waits leave other routes free to copy, and automatic monthly preparation waits for its matching archive route. The release build and all 537 generated integration checks passed for the archive changes.

# 2.2.1 Identify clip selection and bulk actions

Identify clip results now use the same selection controls as Find by date: Select cells, Space, Select / unselect row, Select all visible and Clear selection. Move selected chooses an isolation folder; Delete selected uses Windows Recycle Bin. Exact-file confirmation includes hidden selections and frozen collision-safe move paths. Successful actions remove only completed matches from results. The reference remains protected; incomplete scans and changed files block mutation.

Release builds and generated integration checks cover row selection, filtered bulk selection, clearing hidden choices, cancelled confirmation, selected-only move/recycle, collision handling, changed reference/matches, incomplete scans and compact layouts.

# 2.1.16 incremental Archive restart

Archive persists location candidates and verified source receipts across restarts. A cache check uses protected file identity, length, creation/write/change times; new or changed archive content is hashed, while unchanged videos are not reread just to rebuild the catalog. Continuous watching skips unchanged, previously verified sources before queuing/scheduling them. New arrivals retain full copy/read-back verification; missing or changed copies invalidate protection. Run once continues to verify source content and its archived copies.

Periodic reconciliation waits a full interval after source discovery completes, avoiding immediate repeated scans when discovery exceeds the interval. Idle status reads “Watching for new or changed clips.” Existing version 1 indexes remain valid and receive a one-time cache population. Unsupported file change stamps fall back to full verification. The cache is not a silent disk-corruption audit; unchanged metadata can only establish previously verified protection.

# 2.1.15 visual refresh

Light surfaces now use lavender accents, while dark surfaces use navy with violet highlights. Rounded commands, stronger headings, branded navigation, and clear selected states replace the uniform outlined-button appearance. Configure and Review files are underlined view tabs below a divider separating tool selection; switching views retains previews. Operation status has its own padded surface, and review tables use softer rows and selected colors. High Contrast retains native painting and system colors.

Release build and the generated UX checks validate navigation, analysis-driven tab selection, retained previews, compact separation, and background Archive lifecycle. Refreshed screenshots include light, dark, and compact workspaces. Physical High Contrast, Narrator, and monitor DPI checks remain manual items in acceptance.md.

# 2.1.14 redesign release

The native shell integrates all nine clip actions, independent tracking and Archive configuration, visible presets, exact previews, bounded Activity, Light/Dark/System themes, and persistent progress/cancellation. First use is visible and skippable; Windows startup is opt in. Tray and second-instance activation retain the same services. Archive catalog pages and capacity checks load on request.

Rename, batch shuffle and Prepare Batch execute frozen plans. New reviewed cleanup/identify/batch/naming flows use in-process recovery; Seek retains earlier successful recycling and Archive retains verified copies. Results expose changed files, incomplete scans, retained backups and interrupted work. Transactional cleanup checks physical ownership and never falls back to permanent deletion when recycling fails.

The updater downloads to a unique location, rejects invalid executables, waits for safe service shutdown, and launches a hidden PowerShell replacement helper. Quoted paths are treated literally. Replacement failure restores the original executable when possible and records recovery in `update_error.log`; the next launch surfaces it in Activity.

## Upgrade and recovery checklist

- Back up `%APPDATA%/ClipsManager` and use disposable/backed-up clips for acceptance. Existing presets, numeric action IDs, independent tracking roots, archive settings and indexes retain their formats and meaning.
- Review saved working-folder resolution and defaults. Defaults fill the normal form; Analyze and required file confirmation remain explicit. Archive presets run once. Missing drives remain saved and editable.
- Inspect Activity after interruption. Unfinished jobs are Interrupted, never completed. Recovery paths and transaction failures require attention before another file operation.
- Retain recovery/backup files when a result says restoration was blocked. Forced termination and power loss cannot guarantee transaction rollback.
- Complete the physical accessibility/DPI, production-library, drive-loss/full-drive, actual executable restart and logoff matrix in acceptance.md; those manual checks remain pending. Cold first-time view construction also requires performance acceptance.
- Compatibility forms remain compiled because baseline checks call them. Remove them only after manual parity review and corresponding test migration.

Version 2.1.14 is pushed to main for automatic release publication. The completed generated checks and remaining manual acceptance are documented separately; publication does not certify the pending physical checks.
