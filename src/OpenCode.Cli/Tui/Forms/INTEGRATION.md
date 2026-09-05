# Form composer integration

Status: **mounted in the production Home and session routes** by
`Components/OpenCodeApp.razor`, with live adapter snapshots supplied by
`InteractiveTui.cs`. `SessionClientAdapter.Forms.cs` hydrates the selected session,
its descendants, and location-global forms, then folds canonical `form.created`,
`form.replied`, and `form.cancelled` events. Replies and cancellations call the
actual `SessionHttpClient` methods. No endpoint or wire DTO is reimplemented here.

The initial component-only change was limited to the two Forms subtrees. The
subsequent mount change also modifies the assigned app/root/adapter files. No
generic renderer, backend, or project file was changed by the mounting owner.

## Public API

Namespace: `OpenCode.Cli.Tui.Forms`.

```csharp
public sealed record PendingForm(FormInfo Form, LocationRef Location);
public sealed record FormReplyRequest(
    string SessionId, FormId FormId, LocationRef Location, FormReply Reply);
public sealed record FormCancelRequest(
    string SessionId, FormId FormId, LocationRef Location);
```

`FormInfo`, `LocationRef`, `FormId`, `FormReply`, and the nested `FormAnswer` values
are the actual `OpenCode.Schema` DTOs. The owner remains a string because `global`
is a valid form owner, but is not a `SessionId`. Do not convert it to one.

Both `FormComposer` and `FormOutlet` accept:

| Parameter | Type / meaning |
| --- | --- |
| `Theme` | Required `OpenTui.Blazor.Forms.TerminalFormTheme`, supplied from semantic theme roles |
| `OnReply` | `Func<FormReplyRequest, CancellationToken, Task>?` |
| `OnCancel` | `Func<FormCancelRequest, CancellationToken, Task>?` |
| `OpenExternal` | `Func<string, CancellationToken, Task>?`, host-owned browser action |
| `CopyExternal` | `Func<string, CancellationToken, Task>?`, host-owned clipboard write |
| `ReadClipboard` | `Func<CancellationToken, Task<string?>>?`, host-owned text clipboard read |
| `UnavailableReason` | Optional explicit service/capability failure |
| `Width` | Actual available composer width, **not always 75** |
| `TerminalHeight` | Current terminal height |
| `FocusKey` | Defaults to `form` |

`FormComposer` additionally requires `Request: PendingForm`.
`FormOutlet` instead accepts `Pending: IReadOnlyList<PendingForm>`, `Location`,
optional `Session: SessionId?`, `Descendants: IReadOnlyList<SessionId>`,
`ChildSession`, `ComposerOpen`, and `PermissionPending`.

```razor
@using OpenCode.Cli.Tui.Forms

<FormOutlet Pending="pendingForms" Location="currentLocation"
            Session="selectedSession" Descendants="descendantSessions"
            ChildSession="isChildSession" ComposerOpen="auxiliaryComposerOpen"
            PermissionPending="hasPromptedPermission"
            Theme="formTheme" Width="availableWidth" TerminalHeight="terminalHeight"
            OnReply="ReplyForm" OnCancel="CancelForm" />
```

Use named typed delegates, not `EventCallback` wrappers that return before the
server operation finishes. Successful completion must mean the server accepted
the operation. A missing callback or `UnavailableReason` yields a visible error;
there is no fallback success, local cancellation, automatic reply, or fabricated
answer. Schema-provided defaults are adopted locally but never submitted without
an explicit user action.

## Mounting and priority

`FormAdapter.FirstForRoute(pending, location, session, descendants, childSession)`
implements the source priority:

1. On Home (`session == null`): the first `global` form at the active location.
2. On a root session: its own forms, then each descendant's forms in supplied
   family order, then `global` forms at the active location.
3. On a child-session route: only `global` forms at the active location. The root
   session owns presentation of descendant session forms.

Order within each owner is the supplied pending-list order. Keep each request's
own location for replies; never replace it with the current route location.

Original sources:

- `packages/tui/src/routes/session/index.tsx:205–237`: family/global selection
  and prompt disabling.
- `packages/tui/src/routes/session/index.tsx:1436–1505`: auxiliary composer,
  then prompted permission, then form, then missing-location recovery, then prompt.
- `packages/tui/src/routes/home.tsx:31–33,89–105`: disable Home input while a
  global form is pending; render the form above it in a full-width overlay.

`FormOutlet` suppresses itself while `ComposerOpen` or `PermissionPending` is true.
Pass **prompted** permissions, not automatically approved permission state. Hide
or disable the ordinary prompt whenever the selector returns a form, even when
another higher-priority surface is visible. Do not overlay a second focused input.
For child sessions, pending global forms suppress the automatic auxiliary composer.

The host/app owner must isolate the `form` focus key from ordinary app keybindings
(especially app exit, session interrupt, navigation, prompt submit, and palette).
The existing generic input events then route into `FormComposer.Key`/`Paste`.
Do not put the form inside a modal whose generic Escape handler closes it locally:
Escape is a real form-cancellation operation.

## Geometry and generic primitive limits

The Razor follows `routes/session/form.tsx:743–1154`: left border; inner padding
left 1/right 3/top 1/bottom 1 with gap 1; title/field content inset 1; selected tabs;
numbered choices; four-column multiselect markers; descriptions indented 7 or 3;
custom/text inputs up to 6 rows; footer left 2/right 3/bottom 1.

Field tabs switch to `Field n of m` / `Review` plus completed count when their
24-character labels do not fit the available width. Review labels cap at 40
characters; review height caps at `max(3, terminalHeight - 14)`. Review uses the
generic host's native-width measurement through `TerminalKeyEventArgs.Measure`
and an explicit top-origin line window, not the currently tail-only text scroll
offset. Keyboard navigation adjusts the window after wrapping.

The parent now supplies the original Home overlay placement through the supported
generic `Box.Position` API: `zIndex=2000`, full width, bottom 1, left/right padding 2.
The Home draft remains visible in a read-only composer underneath. Session forms
use the available session-pane width and replace input after prompted permissions.

The generic bridge now supplies pointer events. Field tabs, review tab, choices,
custom-answer rows, and external URLs are wired to the same state/actions as keys.
Footer actions remain keyboard-driven. Choice/review modes use one empty Input
in the footer to participate in the existing focus tree; its placeholder displays
the key hints. Text/custom editing uses the actual answer Input instead.

## State and service lifecycle

- Supports string, number, integer, boolean, multiselect, and external fields.
- Conditions evaluate only earlier visible input answers, with scalar equality or
  multiselect membership. Missing values fail both `eq` and `neq`. Hidden answers
  remain local for revisiting, but are excluded from validation/replies and cannot
  make later hidden-dependent fields visible.
- Required/type checks, length, pattern, format, numeric bounds, item counts, and
  configured-option restrictions apply before reply. Numeric parsing is invariant
  and finite; integers reject fractions. Regex validation uses .NET ECMAScript
  mode; date-time parsing uses .NET invariant parsing, not JavaScript Date parsing.
- Boolean/string-choice single-field forms submit on explicit choice. Text,
  numeric, multiselect, external, and multi-field forms have a review stage.
- Custom multiselect editing replaces the previous custom value, preserves other
  selections, and deduplicates the new custom value. Optional empty multiselect
  answers are omitted. Unknown default selections remain visible for review.
- Browser open/copy must complete successfully before Enter can acknowledge an
  external action. Acknowledgement is an explicit boolean `true` answer. The host
  wires `FormExternalActions.OpenAsync` to the user's explicit browser action;
  failures propagate. Clipboard reading/writing still require a host clipboard
  service; unset delegates report unavailability, not fake clipboard success.
- Reply, cancellation, browser, and clipboard operations share one in-flight
  gate. Repeated keys cannot enqueue duplicate calls. Escape during an operation
  is consumed; it does not claim to cancel a server operation whose outcome is
  unknown. Unmount/request replacement cancels the callback token.
- Only a successful reply/cancel callback freezes the component as sent. Keep it
  mounted until the authoritative pending projection removes it. Errors retain
  local answers and display the callback error. The backend/controller owns event
  hydration, reconciliation, and idempotency.
- Identity is owner + form ID + location. Ordinary rerenders do not reset answers.

Keys: Tab/Shift+Tab change field; arrows or h/l change field outside text editing;
up/down or j/k choose an option; 1–9 choose directly; Enter selects/confirms/submits;
Space toggles multiselect; c copies an external URL; Escape closes custom editing
or requests cancellation. Ctrl+C clears nonempty input before cancellation.
Ctrl+V/Shift+Insert invoke the optional clipboard reader. Text input supports
selection, word movement/deletion, undo/redo, and Shift+Enter newline.

## Verification

The initial component unit passed an isolated .NET 11 CLI dependency build without
warnings. The mounted app also builds; concurrent owners' unrelated warnings/errors
are reported separately rather than hidden. Artifacts are under
`C:\tmp\opencode\forms-compile-20260904-a`. Restore is restricted to a local
source/cache; subsequent builds use `--no-restore`. No tests, app/native execution,
server/API calls, database operations, or commits were performed. Runtime geometry,
pointer behavior, and terminal/IME behavior are not verified.
