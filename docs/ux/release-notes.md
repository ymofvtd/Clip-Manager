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
