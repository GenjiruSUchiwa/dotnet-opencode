# Prompt stash — root integration

Implementation is confined to the new `CLI/Tui/Stash` subtree. No root fields, command registration, existing Dialogs, shared configuration, or project files were changed. This is a real memory-backed JSONL store and production `DialogSelect` component, not a text-only prompt adapter.

## Source and storage

- `packages/tui/src/prompt/history.tsx`: shallow `parsePromptInfo` validation (`text` string and `pasted` array), with all other fields preserved. `PromptStashCodec.ParseHistory` supplies the same tolerant parsing and last-50 cap to the existing history owner; it does not create another history service or history file.
- `packages/tui/src/prompt/stash.tsx`: chronological insertion order, timestamp in Unix milliseconds, clone on push, cap of 50, newest pop, indexed removal, append when untrimmed and rewrite after trimming/removal. Timestamps are not used to sort the stored sequence.
- `packages/tui/src/component/dialog-stash.tsx`: reverse display order, trimmed first-line preview (50 UTF-16 units with an ellipsis), relative time, multiline `~N lines`, two-step `stash.delete`, and reset on selection movement. Filter changes and store revisions also clear confirmation.
- Path follows existing `SessionUiStorage`: `$XDG_STATE_HOME/opencode/dotnet/tui/prompt-stash.jsonl`, or `~/.local/state/opencode/dotnet/tui/prompt-stash.jsonl`. The optional constructor argument is an absolute **state home**, not an arbitrary file path. Never pass the TS state directory or copy production state/shared config.

Construct one `PromptStashStore` per client root. Construction does not read or write files. Explicit `LoadAsync` loads once; await it before enabling stash commands. The dialog can await the same load when mounted. Borrow this store across mounts; closing the dialog only unsubscribes its UI listener.

## Root-owned commands and capture

Register the actual source command IDs (source defaults are unbound; no invented slash aliases):

| Command | Title | Eligibility |
| --- | --- | --- |
| `prompt.stash` | Stash prompt | Loaded, actual prompt text nonempty, and caller can stash |
| `prompt.stash.pop` | Stash pop | Loaded, nonempty stash, caller can replace the current draft |
| `prompt.stash.list` | Stash list | Loaded, nonempty stash; restoration still requires eligibility |

The root must compute `PromptStashAccess(Allowed, AdmissionId, Reason)` from its real editor/admission state at the action boundary. **Uncertain, in-flight, pending, or otherwise bound admission input is not a new editable request.** Set `Allowed = false` when uncertain, including when the ID is unavailable; pass an existing ID when bound. The store rejects a non-null ID even if `Allowed` was accidentally true. Do not remove IDs, reconcile through an empty prompt, auto-retry, or silently issue a new request. This is caller authority, not an automatic admission detector.

For push:

1. Capture the complete existing `PromptEditDocument` and actual pasted-text descriptors on the root renderer dispatcher. `StashPrompt.Capture(document, pasted)` retains URI/data payloads, file/agent/skill descriptors and mentions, shell/normal mode, native metadata, and virtual-mark snapshots. There is intentionally no text-only capture overload or default empty pasted list.
2. Call `store.Push(prompt, access)`. Its returned `Entry` means accepted **in memory**, not saved to disk.
3. Only after memory acceptance, reset the actual composer using its owner operation: input, attachments, marks, selection/cursor, draft/history state, and other source reset behavior. Never let an awaited write clear a newer draft.
4. Observe `mutation.Persistence` and report failure through the root's existing error UI. Preserve the store for memory-only recovery.

History cursor/undo/tab state remain root-owned, not independently serialized by this feature. The owner must capture/reset/restore those through its actual editor/history operations. The stash does not append a submitted history item or manufacture a `SessionPromptInput`.

## Mount and restore

```razor
<DialogStash @key="stashStore" Store="stashStore"
    Theme="ElevatedColors" Backdrop="@DialogColors.Backdrop"
    TerminalHeight="height" ResolveCommand="ResolveDialogCommand"
    DeleteShortcut="@ConfiguredStashDeleteLabel"
    CanRestore="CanRestoreStash" OnRestore="RestoreStashedPrompt"
    OnClose="CloseDialog" />
```

Use the existing configured key resolver and displayed shortcut for **`stash.delete`**. The fallback is source `ctrl+d` only when no resolver is supplied; a supplied resolver returning null does not re-enable a disabled binding. Do not wrap this component in another modal.

`CanRestore` is `Func<StashEntry, PromptStashAccess>`; `OnRestore` receives the complete `StashEntry`. Before returning allowed, validate that the root can restore **all** supplied descriptors. `entry.Prompt.RestoreData()` provides a typed document, pasted descriptors, and preserved source JSON. It rejects admission envelopes, unsupported mode/version/top-level fields, or invalid typed data rather than silently reducing them to text. The lossless `Prompt.Value` remains available to an explicitly capable adapter. Parsing itself remains shallow like TS and does not discard an otherwise valid entry just because this client cannot restore it.

The restore callback must restore the real input, arrays, pasted payloads, virtual marks, shell mode, and owner history/edit state, then move the cursor to buffer end as in the source. Do not discard `StashRestoreData.Pasted` when restoring `Document`. If the current root lacks pasted-text reconstruction, reject those entries with a clear reason; do not substitute the displayed placeholder text. Never submit/send from this callback.

The dialog consumes the entry after the structured callback succeeds, then observes persistence and closes. This deliberately preserves the original entry if an asynchronous native restore fails (TS's restore callback is synchronous). Keep root editing/admission actions disabled during this callback; revalidate admission state immediately before applying the restore if the callback awaits work. An absent callback is an explicit error.

For the pop command, inspect the newest entry, validate/decode and prepare the full restore first. `Pop(access)` consumes synchronously and returns it plus persistence. If the root restore can fail asynchronously, use the same callback-before-`Take(index, expected, access)` sequence as the dialog instead. The `expected` entry protects against consuming a shifted index. Do not reconstruct a new prompt from `Entry.Prompt.Text`.

## Persistence outcomes and limitations

- Mutations are immediately visible in memory. Writes are serialized within this store; `StashWriteResult` distinguishes `Saved`, `Failed`, and `NoChange` and identifies its revision. A successful older revision is not proof that newer changes reached disk.
- Observe `Snapshot.Dirty` / `Snapshot.Error` through `Changed` for root error reporting, including after the dialog closes. Events can originate off the UI thread; dispatch root UI updates. The dialog does this for its own subscription only.
- Write failures retain memory state and an error, not a fake save. After a failure, a later mutation/retry rewrites the complete snapshot instead of claiming an append also saved missing older entries. `RetrySaveAsync` is explicit; `FlushAsync` waits for already queued work. Root shutdown should stop mutations then await `FlushAsync`.
- A missing file is an empty stash. Other read failures permit memory-only use but do not overwrite the unread file; remounting the same store does not retry the read. Keep that store alive to recover unsaved drafts. Do not replace it blindly with another store.
- Valid retained entries are rewritten on load; an all-invalid file is not erased on load. Rewrite uses a same-directory temporary file and replacement. Appends and best-effort persistence are not a durable queue or a cross-process merge/lock guarantee.
- The production selector has no per-row color override. While delete confirmation is armed, this component uses semantic destructive focus colors for the selected row/action and confirmation text. It does not modify shared Dialogs or copy their renderer. This is not exact per-row styling when action focus changes.
- Source timestamps outside the .NET date range display `Invalid date`; preview avoids splitting surrogate pairs. No periodic relative-time timer is introduced.

## Verification

Full CLI build succeeded using the repository-local pinned .NET 11 SDK and isolated artifacts, with **0 warnings and 0 errors**:

```powershell
.\.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass --no-restore -v:minimal
```

Build-only verification. No tests were added, edited, or run. No application/TUI, stash-file reads/writes, live config/state import, API/SSE/network, DB, clipboard, filesystem probes, native verification, or screenshots were run. No commits or delegation. Root mounting and actual prompt/history restoration remain with the root owner.
