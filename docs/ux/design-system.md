# Native desktop design system

The implementation uses WinForms, Segoe UI, native focus/Tab behavior, dock/table/flow layouts and DPI scaling. MainShell owns service lifetimes; a view only renders and configures them.

| Token | Light | Dark |
| --- | --- | --- |
| App background | #F4F6F8 | #12161C |
| Surface/input | #FFFFFF | #1C222B |
| Main text | #17212F | #EDF2F7 |
| Secondary text / control border | #526174 | #B0BECE |
| Primary / white text | #245CC5 | #245CC5 |
| Recycle / white text | #B42336 | #B42336 |

High Contrast uses SystemColors, native highlight selection, and native focus. System theme follows AppsUseLightTheme and Windows preference changes. The restrained blue accent identifies Analyze/primary commands; red is reserved for a consequential recycle confirmation. Controls use one-pixel contrasting boundaries and native focus rectangles rather than decorative low-contrast borders.

Body type: Segoe UI 10.5 pt; workspace headings: 18 pt. Spacing is based on 4/8/12/16/24 logical pixels. Buttons have a minimum 34 logical-pixel height. Text wraps or scrolls; exact paths stay copyable. No emoji, animation decoration or permanently spinning watch indicator is used.

## Components and states

| Component | Behavior |
| --- | --- |
| PathPicker | Editable path, native Browse, Open, Copy, recent paths menu, one-item file/folder drop validation, visible validation text. Missing drives remain saved. |
| SourceFolderList | Add/remove source roots, overlap/link explanation, retained unavailable configuration. Removing a row does not delete clips. |
| FileContextList | Virtual cells; immutable displayed rows; stable key selections independent of filtering/sorting. Readable filename, group, folder/batch, action, duration and size; full paths in the details area. Space and visible Keep/Recycle button provide keyboard alternatives to double click. |
| WorkflowWorkspace | Configure and Review button switchers; pinned Analyze / Review and run commands; persistent results. A changed draft invalidates execution of a prior review. Navigation preserves drafts/results. |
| ArchiveWorkspace | Explicit run mode, independent Fast/Slow selection, advanced units, pinned Start/Pause/Resume/Stop, bounded pending/retry and log projections, asynchronously loaded catalog with 1,000-location pages and explicit capacity checks. |
| ReviewConfirmation | Exact paths, proposed actions, keeper context and recovery explanation. Neutral Cancel is focused, the Enter default and Escape action. |
| JobController / operation strip | Editing, Analyzing, ReadyToReview, Running, Cancelling, Completed, CompletedWithIssues, Cancelled, Failed. Latest snapshot polled every 300 ms. Navigation never owns cancellation. |
| Activity | At most 100 job records, outcome filters, full context/details and log-location access. Interrupted records remain interrupted after restart. |

Default/hover/pressed/focused states use native button behavior with shared borders/hover surfaces. Disabled execution always has configuration, result or busy context nearby. Invalid inputs and unavailable paths expose explanatory text. Essential information is never carried solely by color or hover help.

Duration display: `1h 12m 08s`. Sizes: decimal MB/GB plus exact bytes on recycle confirmation. Times are local `g` format; history keeps DateTimeOffset values. Archive's decimal aggregate MB/s includes content reads, writes and read-back verification.

## Accessibility conventions

Every editable path, selection, numeric setting and review grid has an accessible name. Natural control order establishes Tab order. Native buttons keep visible keyboard focus; Browse has an access key. Space toggles eligible duplicate rows. Filtered selections remain counted and included in final confirmation. Enter never defaults to a destructive confirmation. Escape closes focused editors/confirmations; active work requires explicit Cancel.

Keyboard/Narrator and physical scaling verification must follow acceptance.md. The current generated screenshots and UI tests establish native feasibility; they do not certify Narrator or the full monitor matrix.
