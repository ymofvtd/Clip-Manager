# VideoTrayApp

A Windows system tray app that monitors video folders and helps organize/rename video files.

## Current Version

2.1.14

The native desktop redesign replaces the main window with **Overview**, **Archive**, **Batches**, **Find & clean**, **Rename & shuffle**, **Activity**, and **Settings**. All nine actions remain available. Configure a tool, **Analyze / refresh preview**, inspect exact paths and proposed actions, then **Review and run**. Opening a destination or applying a preset never starts file changes. Results and recovery details stay in Activity. Light, Dark, System and Windows High Contrast share native controls.

Implementation and acceptance evidence are recorded in [plan.md](plan.md), [the redesign audit](docs/ux/bug-audit.md), [screenshots](docs/ux/screenshots), and [the acceptance record](docs/ux/acceptance.md). Physical DPI/Narrator, removable/full-drive, production-library, and real update/logoff acceptance remain pending. Version 2.1.12 is the prior feature baseline; this redesign is published through the main-branch release workflow.

First launch opens visibly with optional setup; startup registration is opt in. Clicking **X** follows **Settings → General → Close window to tray**. Tray **Exit** cancels foreground work, awaits recovery, and drains background Archive. Left click or a second app instance activates the existing window.

## Watch multiple folders

Open **Settings → Duration tracking** or the tray's **Duration tracking** entry. Add or paste each root, remove roots you no longer want, then **Save tracking roots**. Removing all roots disables tracking. The working folder for naming/shuffle actions is configured separately in Overview.

Each watched folder and its immediate subfolders gets its own total-duration marker, such as `90M30S.txt`, based on the videos directly inside that folder. Totals refresh automatically when clips are added, modified, renamed, moved or deleted. Empty folders have their duration markers cleared; other text files are preserved. Overlapping watched folders are supported. Tracking pauses for preset operations and Shuffle Batches, then refreshes every watched folder afterward.

The list is saved in `%APPDATA%/ClipsManager/watch_folders.json` and restored on restart. Your existing single-folder setting is migrated automatically. Unavailable folders stay in the saved list and are retried when you save the list, restart the app, or finish a preset operation.

## Archive

Open **Archive** from navigation or the tray. Add one or more source folders and choose a separate archive destination. All nested videos are included; overlapping source roots are deduplicated. Links/junctions and linked ancestors are excluded. The destination and sources cannot contain one another. Archive settings are separate from duration tracking. Advanced scheduling exposes interval, settling and aggregate I/O units. Capacity checks run asynchronously and report unknown/unavailable drives explicitly. Catalog details are loaded on request in pages of up to 1,000 locations.

Archive **copies and verifies** clips; it never moves, renames, deletes, recycles, or shuffles source or existing archive files. Each new clip is copied to an owned temporary file, flushed, read back, and verified by size, duration, and full SHA-256. Sources are held open against content writes/deletion during processing. Only a verified copy is published, with a full-hash filename and the original extension; occupied filenames with different content are reported and never overwritten. Existing destination videos are indexed recursively under their current names, including Shuffle Batches layouts. Identical content under another extension is reused. Original names, source paths, source-root labels, and archive locations remain in a versioned JSON index.

Choose **Run once** (Watch continuously unchecked) or **Watch continuously**, independently of **Fast** or **Slow**. Fast runs one clip at a time without an intentional interval or I/O cap. Slow defaults to a 120-second minimum interval between clip starts and an optional 20 MB/s aggregate limit across application-controlled content reads and writes, including hashing and verification. MB/s means 1,000,000 bytes per second; actual copying is slower because verification adds passes. Metadata and filesystem caching affect observed disk usage. Settling defaults to 10 seconds with unchanged size/time; locked or growing clips remain pending.

Use **Start**, **Pause**, **Resume**, and **Stop**. Closing the window keeps an active run in the tray; Exit stops background work safely. Explicitly enabled continuous watching resumes on later app launches. Stop disables automatic resume; Start remembers the selected mode again. The recursive watchers queue/debounce arrivals, handle new subfolders, and reconcile every five minutes and after overflow/errors. The event queue is bounded at 4,096 paths; overflow schedules reconciliation. Unavailable sources receive backoff/reconciliation, and an unavailable/full destination suspends copying with a persistent status warning. Run-once failures stop retrying a clip after three attempts; Start rediscovers unfinished work.

The status window shows queued clips, byte progress, copied/verified and already-archived counts, failures/retries, last success, warnings, and a bounded log. Foreground clip operations take priority, interrupt an incomplete background copy, and requeue it; watchers stay subscribed. Completed verified copies survive cancellation. Restart reconciliation recovers a finalized copy even if publication was interrupted before its index save. Only exact temporary paths recorded as owned by this archiver are cleaned up. Archive writes use a non-video temporary extension to avoid repeated duration refreshes. Shuffle Batches can still move/rename archive clips; reconciliation finds their new locations and recopies missing content when a source survives.

Settings, per-destination indexes, and the bounded shutdown log are stored atomically under `%APPDATA%/ClipsManager` (`archive_settings.json`, `archive_index_*.json`, `archive_log.json`). Archive presets support source lists and speed settings and always run once. Older Target/UseWorkingFolder and Destination settings resolve to the same folders with the new copy-only behavior. Completed copies are independent of preset rollback.

Archive saves its file cache across restarts. Startup and periodic reconciliation enumerate paths and check file identity, size, creation/write/change times, hashing only new or changed archive files. Continuous watching also skips an already verified source when both it and an archived copy retain their protected-file stamps; unchanged historical clips do not take a Slow scheduling slot. New clips still receive full copy and read-back verification, and changed or missing copies trigger checking/recovery. **Run once** explicitly rechecks source content and its archived copies. The first launch with an older index needs one content scan to populate the new cache. Filesystems without reliable change stamps fall back to full checking. Metadata checks are not a fresh integrity audit and cannot detect silent disk corruption that leaves all file metadata unchanged.

A queued clip is not protected until a copy is verified. Clips deleted before processing, while the app is closed, or while the destination is unavailable can still be lost. Slow mode leaves a longer gap; pending and unavailable status remain visible.

## Seek duplications

Open **Find & clean → Seek duplications** and configure directories A and B. Subfolders are included by default. Analyze finds exact size/duration/SHA-256 identity groups represented in both selections and includes every matching path. Copies entirely within one selection are excluded. Overlapping paths appear once. Hard-link aliases are labeled as the same underlying file and stay Keep; they do not represent independently reclaimable content. Re-encoded or edited clips are outside this action's scope.

Every row starts as **Keep**, with neither directory given priority. The table shows group, A/B and folder context, filename, size, duration, and modified time; selected-file details reveal full paths and content/physical identities. Use **Open clip**, **Open containing folder**, or **Copy path**, then toggle individual copies with **Keep / Recycle selected row** or Space. Leave at least one kept copy per group. Filtering and sorting preserve hidden choices; refreshing preserves choices only for unchanged file identities. **Review and run** confirms exact selected paths, keeper context, and known content bytes before recycling.

Both kept and selected copies are verified again immediately before recycling. A kept copy stays open against writes/deletion throughout its group. A missing/changed keeper skips that group; a changed selected copy stays untouched. Only the Recycle Bin is used; unavailable recycling reports an error with no permanent-delete fallback. Cancellation stops further deletions and reports completed versus untouched copies; earlier successful recycling is not automatically reversed. Unreadable paths and cancelled scans are reported separately from a complete scan with no duplicates. Presets can save A/B paths and recursion, but always open the individual review and final confirmation.

## Identify clip

Open **Find & clean → Identify clip**, select a single reference video, and choose the search folder. Subfolders are included; junctions and symbolic links are skipped. Clips match only when their full SHA-256 content hash and duration match the reference, regardless of filename.

Choose **Inspect results only**, **Move matches**, or **Recycle matches**, then Analyze. The reference and its physical aliases are excluded. Moving preserves filenames and freezes any required collision suffixes in the preview; existing files are never overwritten. Incomplete scans block mutation. Reviewed actions verify files and hold the reference against modification, using in-process transaction recovery on failure/cancellation. Successful recycling is recoverable through Windows Recycle Bin.

## Remove duplicates

Open **Find & clean → Remove duplicates** and choose a folder. The scan includes subfolders and skips junctions and symbolic links. Clips are grouped only when file size, duration, and full SHA-256 content hash all match, even if shuffle/rename changed their filenames. Unique file sizes are skipped to avoid unnecessary hashing. Re-encoded or edited versions are not treated as exact copies.

Analyze shows **Keep** and **Recycle** paths using the first full path alphabetically as keeper. **Review and run** confirms exact paths. Incomplete scans block mutation. Both keeper and targets are verified again; reviewed cleanup stages removals and attempts transaction rollback on cancellation/failure. There is no permanent-delete fallback.

## Prepare Batch

Open **Batches → Prepare Batch** and configure the source, destination parent, selection and target minutes. Analyze freezes a random source selection and resolves the highest numbered immediate `c_` folder, or the exact new batch folder. It simulates duplicate cleanup/refill without creating folders, backups, renames or recycled files. The review shows preserved starters, proposed recycling/keepers, backup paths, exact move destinations and planned duration. Destination copies are kept first; otherwise the first source clip in the shuffled order is kept. Files and the reviewed starter inventory are verified again before execution.

After removing duplicates, the batch is refilled from the same shuffled order and checked again until no duplicates remain. Only then are the final clips backed up to the source's `Backup` folder and moved to the destination with numeric filenames (`1`, `2`, `3`, ...) from shortest to longest. Existing destination files are not renamed, occupied numbers are skipped (across extensions), and video extensions are preserved. Backups retain the original filenames. Equal-duration clips keep their selected order. Backups, source subfolders and unselected source clips are excluded from cleanup. The last clip may take the batch past the requested duration, as before. Scan or cleanup errors stop preparation before backup and moving; cancellation stops further work. The source and its Backup folder cannot be used as the destination.

## Shuffle Batches

Open **Batches → Shuffle Batches**, choose your archive root, and set maximum minutes per batch. Analyze creates a fixed review without changing files or creating folders. **Review and run** uses that exact mapping. Every analysis pools supported videos recursively, including existing batches and loose clips. `Backup` folders and links/junctions are excluded.

All clips are randomized together, then packed into batches within the duration limit. Whole clips are kept intact; a clip longer than the limit gets its own folder, and some batches can be shorter. Existing immediate folders containing archive clips (and empty `batch_` folders) are reused in alphabetical order. New `batch_0001`, `batch_0002`, etc. folders are created when needed, skipping occupied names. Nested clips are placed directly into the batch folders. Unused folders and non-video files are kept. Within each batch, clips receive padded numeric filenames (`0001`, `0002`, ...) from shortest to longest, preserving their extensions and avoiding existing files and folders. Equal-duration clips retain their shuffled order. Sorting uses the durations already read during the scan.

This action moves and renames clips only: it does not remove duplicates, recycle clips, or create backups. It reads each duration once, without hashing or copying video content. Cancellation or errors reverse the moves and newly created folders; if another process prevents recovery, the error reports the problem and retains the affected files. Forced termination or power loss is outside this in-process rollback guarantee. Run it again after finishing the batches to remix the whole archive. **Manage presets...** can save the archive root and duration, including a default or right-click selection.

Duration tracking is paused during reshuffling and refreshed afterward. It replaces only duration marker files named like `90M30S.txt`, preserving other `.txt` files.

## Presets

Each workspace has visible **Apply preset**, **Save preset**, and **Manage presets** controls; right-click access remains available. **Settings → Presets** and the tray menu open the same manager. Use **Add preset**, **Edit**, **Duplicate**, **Rename**, **Delete**, and **Set as default**. **Always ask (Current)** opens normal configuration. Deleting a default restores Always ask. Settings are saved atomically in `%APPDATA%/ClipsManager/presets.json`; unavailable drives remain editable.

Saved presets can use the current working folder or a fixed target. A saved default fills the normal configuration without running. Analyze and the required exact-file confirmation remain explicit. Archive presets always run once; Seek duplications always requires individual choices and a final confirmation.

The workspace context menu reloads its presets each time it opens. Choosing one fills configuration without changing the saved default or executing work. Empty menus state that no presets are saved.

Prepare Batch presets include a destination parent folder, a custom new batch folder name, duration and selection. Every preset automatically uses the highest numbered immediate `c_` subfolder (for example, `c_59` over `c_58`), preserving your starter clips and including their duration when refilling. The folder is discovered when you run the preset, so it does not need to exist when saving. No Main or create-folder checkbox is needed; target and destination parent may be the same folder. If no `c_` folder exists, a new folder is created automatically: leave the name blank for the next available `batch_1`, `batch_2`, etc., or enter your own name. Occupied custom names receive a numeric suffix. The confirmation shows the exact folder to use or create; if it disappears or becomes occupied after review, the run stops safely instead of switching destinations. Older presets automatically adopt this behavior. **Add random 30m archives** starts a preset with 30 minutes, recursive archive selection; choose your folders before saving it. Numbered MP4 selection uses top-level numbered MP4 files. AllVideos includes all top-level supported videos; Archives also includes subfolders, excluding Backup, destination folders, and links. All selections are randomized. Every Prepare Batch preset numbers newly prepared clips from shortest to longest, skipping occupied numbers and leaving starter filenames unchanged. Older batch naming settings are superseded by this duration ordering. Backups retain the original filenames.

Except for Archive and Seek duplications, reviewed workspace operations validate paths and attempt in-process rollback of moves, renames, staged removals, new backups and batch folders on failure/cancellation. Duplicate removals are staged until completion, then recycled with temporary recovery copies retained until recycling succeeds. Duration tracking pauses for foreground clip work and refreshes afterward. Locks, external replacements or changed paths can block recovery; results and Activity reveal retained files. Forced termination or power loss is outside this guarantee. Legacy compatibility adapters retain their older manual contracts. Archive retains completed verified copies; Seek duplications retains earlier successful recycling after cancellation.

## Rename & shuffle

Choose **Shuffle & Name**, **Name by 10s**, or **Shuffle Random** under **Rename & shuffle**. Configure the working/fixed folder and relevant start, padding, prefix or random-name length. Numbering advances by ten; it is not a duration filter. Analyze shows each original and proposed filename, preserving extensions. Random mappings remain fixed until you refresh the preview. Execution rejects changed files and occupied destinations and attempts rollback on cancellation/failure.

## Activity, updates and recovery

The operation strip keeps progress and Cancel available across navigation; Archive has a separate persistent indicator. **Activity** retains at most 100 outcomes with exact context and recovery paths. Unfinished records restore as Interrupted. UI preferences are separate from presets, tracking and Archive settings; corrupt preferences retain a recovery copy.

**Settings → Updates** checks only on request. Installing an update downloads first, blocks conflicting foreground work, drains Archive and restarts through a hidden helper. Replacement failure restores the old executable when possible and writes `update_error.log` with backup details; the next launch exposes that log in Activity and Overview. The acceptance record lists the completed checks and remaining manual validation.

## Manual Build (Release .exe)

You need the .NET 10 SDK installed.

```powershell
# From the repo root
dotnet publish VideoTrayApp/VideoTrayApp.csproj -c Release
```

The portable single-file executable will be here:

```
VideoTrayApp/bin/Release/net10.0-windows/win-x64/publish/ClipsManager.exe
```

This `.exe` is self-contained (includes the .NET runtime). You can copy it anywhere and run it without installing anything else.

**Tips:**
- The file is ~110MB because it bundles the full runtime.
- Right-click the exe → Properties → Details to see version, etc.

## Releasing a New Version (Manual)

1. Update the version in `VideoTrayApp/VideoTrayApp.csproj`:
   ```xml
   <Version>1.0.2</Version>
   <AssemblyVersion>1.0.2.0</AssemblyVersion>
   <FileVersion>1.0.2.0</FileVersion>
   ```
2. Commit the change.
3. Tag and push:
   ```bash
   git tag v1.0.2
   git push origin v1.0.2
   ```
4. On GitHub, go to Releases → the new tag should appear (or create one manually and attach the exe).

## Automated Releases (Recommended)

This repo uses GitHub Actions to build and publish releases automatically.

### How to trigger an automated release

**Option A – Using Git tags (recommended)**

```bash
# Update version in .csproj if you want
git add .
git commit -m "chore: prepare v1.0.2"
git tag v1.0.2
git push origin main --tags
```

Pushing the `v*` tag will:
- Build a clean Release `VideoTrayApp-1.0.2.exe`
- Create a GitHub Release
- Attach the executable

**Option B – Manual trigger**

1. Go to your repo on GitHub → **Actions** tab
2. Select **"Build and Release"** workflow
3. Click **"Run workflow"**
4. (Optional) Enter the version (e.g. `1.0.2`)
5. Run it

After it finishes, the Release will appear under **Releases** and the exe will be attached.

### Workflow file

See `.github/workflows/release.yml`

## Notes

- The app uses `PublishSingleFile` + `SelfContained` so one exe works on any Windows 10/11 machine (x64).
- No .NET runtime needs to be pre-installed on the target computer.
- The tray app hides itself and lives in the notification area.

## Development

```powershell
dotnet build
# or run the project
dotnet run --project VideoTrayApp
```

Run the clip operations, folder tracking and settings integration checks (uses disposable generated clips):

```powershell
dotnet run --project VideoTrayApp.Tests -c Release
```
