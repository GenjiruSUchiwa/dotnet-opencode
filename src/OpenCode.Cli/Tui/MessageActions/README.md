# Message actions and transcript presentation handoff

This subtree supplies components and operations. It does not mount a root dialog
or change `OpenCodeApp`.

## Root wiring

Construct `MessageActionsController` with the host's `ITextClipboard`, a lookup
of the currently projected message by `MessageTarget`, and real services:

```csharp
var services = MessageActionServices.FromClient(client, ReportError, ReconcileSession) with
{
    Jump = JumpToMessage,
    RestorePrompt = RestoreProjectedUserPrompt,
    CanMutate = CanChangeSession
};
var actions = new MessageActionsController(host.Clipboard, ReadProjectedMessage, services);
```

`FromClient` uses the existing canonical `SessionHttpClient.StageRevertAsync`;
it neither discovers another server nor starts a process. Revert appears only
with both staging and reconciliation consumers. `RestorePrompt` receives the
complete canonical `UserMessage`, including files, skills, and agents. Preserve
those fields when restoring the composer's draft; do not replace them with only
the text. Restoration occurs before staging, matching the source dialog.

Fork is not fabricated: no fork method was present in the inspected HTTP client.
It remains absent unless the owner supplies both `ForkBefore` (the canonical
before-message boundary) and `OpenFork`. It appears only for user messages.

Mount the supplied dialog from the root's message-action branch:

```razor
<DialogMessage Target="SelectedMessageTarget" Controller="MessageActions"
               Theme="DialogColors" TerminalHeight="_height"
               CancellationToken="AppLifetimeToken" OnClose="CloseDialog" />
```

Expose these new `SessionTranscript` parameters:

- `MarkdownMode`: `MarkdownPresentation.Rendered` or `.Source`. Bind it to
  `session.markdown`; the root remains the setting owner.
- `OnMessageActions`: receives a `MessageId` from a user-message card click.
  Combine it with the selected Session ID and open `DialogMessage`.
- `OnCopyCode`: receives the exact code block contents, without fences. Route
  it to `MessageActionsController.CopyCodeAsync`. Code copy controls only appear
  when this callback is connected.

Whole-message copy uses canonical content: user text, assistant text parts joined
by newlines, or system/synthetic text. It deliberately excludes reasoning, tools,
rendered labels, timestamps, and attachment display labels, matching
`routes/session/dialog-message.tsx`. Copy failure leaves the dialog open and
reports through the required error sink. Revert starts and then closes the dialog;
its lifetime/error reporting must remain owned by the root, not the closed view.

## Generic selection and clipboard

`OpenTui.Blazor.ITextClipboard` is the only clipboard contract used by controllers.
`OpenTuiHost.Clipboard` defaults to `WindowsTextClipboard`; another host may inject
its own implementation. Construction does not touch the clipboard.

`NativeClipboard` writes Windows `CF_UNICODETEXT` only on an explicit copy action.
It uses source-generated `LibraryImport`, a valid console/window owner, movable
global memory, `GlobalLock`/`GlobalUnlock`, ownership transfer on successful
`SetClipboardData`, and unconditional clipboard close. Failed transfers free the
allocation. No WPF, helper process, or third-party native DLL is used.

Plain user text, Markdown source, and code blocks opt into `TuiText.Selectable`.
Drag selection uses the existing cell-width/grapheme layout, suppresses click
activation, and is rendered through native drawing calls. Ctrl+C copies an active
text selection before the app's prompt-clear/exit command; Escape clears it first.
Clipboard errors are reported without clearing the selection. Source edits or
removed text nodes clear stale selections; resizing preserves logical indexes.

Selection is currently **within one plain text block**, with character or no
wrapping. Styled/word-wrapped rendered Markdown does not opt into pointer
selection: the existing native wrapper does not expose its visual line map.
Whole-message and whole-code copying remain available independently of that
limitation. No approximate word-wrap hit testing or syntax highlighter is claimed.

## Source alignment

- `routes/session/dialog-message.tsx`: action labels/order, canonical copy text,
  user-prompt restoration and staged revert.
- `routes/session/dialog-fork.tsx`: before-message fork and original prompt handoff.
- `routes/session/index.tsx`: source/rendered Markdown concealment, user-card
  action opening, reasoning-group duration and latest summary behavior.
- `context/thinking.ts`: only the complete leading `**title**` block is treated
  as reasoning disclosure metadata. Arbitrary first lines are no longer promoted
  to titles; reasoning duration sums actual part spans.

All validation in this pass is build-only. No clipboard operation, native runtime,
API request, UI launch, or test was executed.
