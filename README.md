# VideoTrayApp

A Windows system tray app that monitors video folders and helps organize/rename video files.

## Current Version

2.1.5

The window title displays the installed version. Clicking **X** hides the window to the tray on the first click; use the tray menu's **Exit** to quit.

## Archive

Click **Archive** in the main action buttons or tray menu, choose the source folder, then choose the destination. All supported video files directly in the source folder are moved; subfolders and non-video files stay in place. There is no duration limit. Filename collisions receive a suffix; existing files are never overwritten.

Archive identifies exact duplicates using file size, duration and SHA-256, then automatically sends extra copies to the Recycle Bin, preferring clips already in the destination. Finally, all remaining destination clips receive random filenames for a fresh shuffle. The destination scan and shuffle also include only top-level clips. Cancellation or errors stop further work and report progress; clips already moved remain in the destination. Same-folder transfers and junction/symlink roots are rejected.

## Identify clip

Click **Identify clip** in the main window or tray menu, select a single reference video, and choose the folder to search. Subfolders are included; junctions and symbolic links are skipped. Clips match only when their full SHA-256 content hash and duration match the reference, regardless of filename.

Review the matching paths and choose **Delete matches** (sends them to the Recycle Bin), **Move to folder...**, or **Cancel**. The selected reference clip is excluded. Moving preserves filenames and adds a numeric suffix when a name already exists; existing files are never overwritten. Matches are verified again before applying actions. Progress supports cancellation, and the result reports completed actions and errors.

## Remove duplicates

Click **Remove duplicates** in the main window or tray menu and choose a folder. The scan includes subfolders and skips junctions and symbolic links. Clips are grouped only when file size, duration, and full SHA-256 content hash all match, even if shuffle/rename changed their filenames. Unique file sizes are skipped to avoid unnecessary hashing. Re-encoded or edited versions are not treated as exact copies.

Review the **KEEP** and **RECYCLE** paths, then click **Delete duplicates** to send extra copies to the Recycle Bin. The first full path alphabetically in each group is kept. Both the kept clip and duplicates are verified again before deletion; if the kept clip is missing or changed, the entire group is left untouched. Cancellation stops further deletions, and the result reports completed actions, untouched clips, and errors.

## Prepare Batch

Select the source, destination and desired duration. Prepare Batch shuffles the numbered MP4 clips in the source folder once before selecting the batch, preserving their filenames. It checks the selected clips together with videos already in the destination, and automatically sends exact duplicates to the Recycle Bin using the same size, duration and SHA-256 checks as Remove duplicates. Destination copies are kept first; otherwise the first source clip in the shuffled order is kept. Files are verified again before recycling.

After removing duplicates, the batch is refilled from the same shuffled order and checked again until no duplicates remain. Only then are the final clips backed up to the source's `Backup` folder and moved to the destination. Backups, source subfolders and unselected source clips are excluded from cleanup. The last clip may take the batch past the requested duration, as before. Scan or cleanup errors stop preparation before backup and moving; cancellation stops further work. The source and its Backup folder cannot be used as the destination.

## Presets

Click **Presets** to manage saved settings in a tab for each clip action. **Always ask (Current)** is the initial default and keeps the existing prompts. Use **Add preset**, **Edit**, **Delete**, and **Set as default**; choosing Always ask restores manual prompts. Deleting a default preset also restores Always ask. Settings are saved in `%APPDATA%/ClipsManager/presets.json`.

Saved presets can use the current working folder or a fixed target. Clicking an action with a saved default shows its settings and asks **Review the default preset settings, proceed?** Once confirmed, it runs without further configuration dialogs, including the saved move/recycle choice for Identify clip and duplicate cleanup for Remove duplicates. Declining makes no changes.

Prepare Batch presets include destination, optional new batch subfolder, duration, selection, and naming. **Add random 30m archives** starts a preset with 30 minutes, recursive archive selection, and numbering by tens; choose your folders before saving it. Numbered MP4 selection preserves the existing behavior. AllVideos includes all top-level supported videos; Archives also includes subfolders, excluding Backup, destination folders, and links. All selections are randomized. Preserve, NumberByTens (start/padding/prefix), and Random naming apply only to newly prepared clips. Backups retain the original filenames.

Preset runs validate paths before execution and reverse their moves, renames, staged removals, newly created backups, and batch folders on errors or cancellation. Duplicate removals are staged until the operation completes, then sent to the Recycle Bin with temporary recovery copies retained until all recycling succeeds. Duration tracking is paused during these operations. If another process locks or changes affected paths and prevents rollback, the error reports incomplete recovery and retains recovery files; forced application termination or power loss is outside this in-process rollback guarantee. Manual Always ask operations keep their existing partial-progress behavior.

## Manual Build (Release .exe)

You need the .NET 10 SDK installed.

```powershell
# From the repo root
dotnet publish VideoTrayApp/VideoTrayApp.csproj -c Release
```

The portable single-file executable will be here:

```
VideoTrayApp/bin/Release/net10.0-windows/win-x64/publish/VideoTrayApp.exe
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

Run the Identify clip integration checks (uses disposable generated clips):

```powershell
dotnet run --project VideoTrayApp.Tests -c Release
```
