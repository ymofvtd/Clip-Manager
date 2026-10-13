# Redesign acceptance record

Date: 2026-10-13. All fixtures in the automated checks are generated under a unique temporary directory. No production library is used.

Version 2.1.15 visual refresh: Release build and portable publish succeeded without warnings or errors; all 90 UX checks passed. Added checks cover analysis selecting Review files, switching Configure/Review without losing results, and compact separation of tool choices and view tabs. Light, dark and compact screenshots were regenerated and visually inspected. This refresh did not rerun the full domain suite or certify physical High Contrast/Narrator/multiple-monitor DPI behavior.

## Reproduce

```powershell
dotnet build -c Release
dotnet run --project VideoTrayApp.Tests -c Release -- --ux-only
dotnet run --project VideoTrayApp.Tests -c Release
dotnet publish VideoTrayApp/VideoTrayApp.csproj -c Release
```

UxChecks emits measured timings/memory and regenerates native captures in docs/ux/screenshots. Domain checks retain the original baseline scenarios: archive copy identity/publication/crash recovery, bounded watcher queue, foreground/background priority, changed keeper/target guards, actual Recycle Bin operation, cancellation/recovery, numeric preset migration, 4,000-clip reshuffle and tracking roots.

## Automated coverage

- Read-only rename, batch selection/refill and archive shuffle analysis; exact reviewed execution mappings.
- Changed-file review rejection, new-starter inventory rejection, cancellation after staging with original byte/path recovery, and external staging/recovery ownership protection.
- Corrupt UI preferences retain the original; disconnected paths round-trip independently of archive settings.
- Bounded history restores unfinished operations as Interrupted; unsupported schema versions warn and retain originals. Background Archive runs persist shared start/stop summaries with verified counts and issue details.
- Visible skippable first launch; all nine actions reachable; native named-event activation; saved Archive watch resumption, close-to-tray worker retention, Stop persistence, draining shutdown and repeated disposal.
- Stable hidden selection and unchanged-identity refresh with 10,000 results; virtualized 50,000-row catalog; foreground command rejection and warm navigation during 100,000 progress reports.
- Native Light/Dark/System HWND captures, compact Archive, every workflow review, presets and exact confirmation. The app-only capture uses PrintWindow, so overlapping desktop windows cannot enter the artifacts.
- Disposable PowerShell updater failure with quoted/literal paths restores the original executable fixture, rejects invalid packages, and records recovery without evaluating path contents. No real installed executable is replaced by the test.

Final generated-fixture measurements on this Windows/.NET 10 workspace (96 DPI): first screen 158?183 ms; cold Archive construction about 1.0 seconds; cold tool destination construction 0.36?0.80 seconds; warm Overview/Activity interaction 13?43 ms; 50,000 virtual rows 55?103 ms, about 73?86 MB process private memory after constructing all views. The warm-navigation assertion remains below 100 ms during 100,000 progress reports. Cold construction remains a documented performance limitation for release acceptance; the navigation heading paints before a new view is constructed. These are local generated-data measurements, not physical-library throughput promises.

The full suite passed **275 checks** (210 retained baseline domain checks plus 65 redesign checks). Release build passed with zero warnings/errors; self-contained win-x64 publish passed. The latest UI polish also passed all 65 redesign checks independently. Screenshot capture renders each application's HWND independently of desktop focus; the minimized legacy window could not produce a useful capture and its blank image was omitted. A baseline visual capture remains a manual audit item.

## Manual matrix still required before a production redesign release

| Scenario | Current evidence | Remaining acceptance |
| --- | --- | --- |
| Themes | Light/Dark/System native screenshots | Windows High Contrast visual inspection |
| DPI/text | Default and compact native layouts | 100/125/150/200% physical DPI, mixed monitors, Windows text scaling, compact + maximum scale |
| Keyboard/assistive tech | Native names/focus/Tab order; Space selection; safe confirmation default | Complete each workflow keyboard-only and with Narrator; verify meaningful announcements |
| Drive loss/full | Existing simulated unavailable/full-drive checks | Real removable-drive disconnect and actually full destination on backed-up fixtures |
| Libraries | Generated 4,000 clips, 10,000 result rows, 50,000 catalog rows | Production-sized backed-up video library, actual throughput/startup/cancel latency |
| Updating/shutdown | Download orchestration, drain/exit guards and disposable replacement-failure recovery | Real executable replacement/restart and Windows logoff during slow I/O/recovery |
| Cleanup of old UI | Production Program points only at MainShell | Remove compatibility surfaces and update their baseline tests after final manual parity review |

Release 2.1.14 is published through the main-branch workflow as requested. Passing generated-data checks does not certify the pending manual matrix. A portable executable is available for that validation. Recovery after forced termination remains outside the in-process rollback guarantee. Restarted Activity explicitly marks unfinished work Interrupted.

## Final local build

Release executable: `VideoTrayApp/bin/Release/net10.0-windows/win-x64/publish/ClipsManager.exe`, FileVersion `2.1.14.0`. The publish bundles .NET 10 and requires no separate runtime installation. The version bump and redesign are committed and pushed to main for automatic release publication; the manual matrix above remains open.
