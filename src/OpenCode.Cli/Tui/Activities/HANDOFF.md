# Session activity panes

Only `CLI/Tui/Activities` is changed. No root mount, Core/Jobs, Shell/Session service, API, shared theme, or reusable OpenTui.Blazor file is edited.

## Root mount

Use `SessionActivities` as an inline composer panel, not a Modal. It owns the two-tab header and the source left-border/padding geometry. The source file is `routes/session/composer/shell-tab.tsx` (singular), alongside `subagents-tab.tsx`.

```razor
@using OpenCode.Cli.Tui.Activities

<SessionActivities @key="(client, sessionID)" @ref="activities"
    Client="client" SessionId="sessionID" CurrentSession="routeSessionID"
    Open="activityOpen" DefaultTab="ActivityTab.Subagents"
    Theme="resolvedTheme.Elevated" Width="composerWidth" TerminalHeight="height"
    Sessions="familySessions" ActiveSessions="activeSessions" Messages="sessionMessages"
    Permissions="pendingPermissions" Forms="pendingForms" Shells="locatedShells"
    Jobs="authoritativeJobs" FamilyComplete="familyIsComplete"
    SessionError="@sessionError" ShellError="@shellError"
    ResolveCommand="ResolveComposerCommand"
    OnFocusRequested="FocusTerminalKey" OnClose="CloseActivities"
    OnOpenSession="OpenSession" OnFocusSession="FocusSessionRow"
    OnRequestMessages="LoadSessionMessages"
    OnFocusShell="FocusShellRow" OnOpenShell="OpenShellDetails"
    OnShellObserved="MergeShellObservation" OnShellRemoved="RemoveShellObservation"
    OnRefresh="RefreshActivities" />
```

Both panes can also be mounted separately as `ShellsPane` and `SubagentsPane`, using their `Active` parameter and `OnSwitchTab(int)` callback. They render at most five list rows, matching source, and preserve family traversal/input ordering. No dedicated terminal font, raw color palette, fake process model, or copied global dialog is introduced: they use real Box, ScrollBox, TuiText and the host's semantic ThemeTokens.

### Minimal typed data

- `SessionHttpClient Client`: the existing authenticated client, never a new connection/discovery service.
- `SessionId SessionId`: Session whose activity/family is shown. Optional `CurrentSession` marks the actual routed Session.
- `IReadOnlyList<SessionInfo>? Sessions`: the root's merged known family, including the anchor and known ancestors/descendants in source display order. Do not pass only a top-level session-picker page and claim it is complete.
- `IReadOnlyDictionary<string, SessionActive>? ActiveSessions`: the real session.active projection. Null means unknown/unloaded, not idle. A known absent ID is inactive; running entries override stale previous outcomes.
- `IReadOnlyDictionary<SessionId, IReadOnlyList<SessionMessage>>? Messages`: optional real message projections for selected-child details. Messages are not used to guess whole-Session running/completion state.
- `IReadOnlyList<PermissionRequest>? Permissions` and `IReadOnlyList<FormInfo>? Forms`: optional actual pending requests. Matching Session IDs produce “approval required”/“input required”, not a title/tool-name heuristic.
- `IReadOnlyList<LocatedShell>? Shells`, where `LocatedShell(ShellInfo Info, LocationRef Location)` carries the **execution** Location from the original API/event envelope. Commands are associated through the real `metadata.sessionID`, never by name or cwd.

`FamilyComplete=false` is the default. An empty family view then says “in the supplied family” instead of claiming the host has loaded every child. Use `SessionError`/`ShellError` for root source errors; unloaded data does not become a fake empty successful catalog.

Shell list API is running-only. Retain root shell-created/exit observations, or obtain known retained IDs through GetShellAsync, if the completed view is required. The pane does not fabricate a terminal ShellInfo when a command disappears, and does not build one from an incomplete history message.

### Optional Job projection

There is no invented Job HTTP endpoint. Foreground/background and blocking information is only shown when the root supplies an authoritative `ActivityJobProjection`:

```csharp
ActivityJobProjection(
    string Id, string Type, ActivityJobState State, SessionId OwnerSessionId,
    ShellId? ShellId = null, SessionId? ChildSessionId = null,
    ActivityJobMode? Mode = null,
    IReadOnlySet<SessionId>? BlockingSessions = null,
    string? Error = null)
```

State is Running/Completed/Error/Cancelled. Mode is Foreground/Background, or null when unknown. Source job blocking means the job is **blocking a Session**, not that a child is waiting for permission; these are displayed separately. Derive this projection only from the actual host Job adapter/state, not command text, title patterns, arbitrary shell metadata, or an assumed relationship between “running” and “background”. Omit it when transport/projection support is unavailable.

Optional `Func<ActivityJobProjection, CancellationToken, Task> CancelJob` exposes an actual host-provided cancellation action only when supplied. It creates no URL and is not substituted with a made-up HTTP call. Session interrupt and shell terminate remain separate canonical Client operations.

## Callbacks and shared feed

```csharp
EventCallback<string> OnFocusRequested;
EventCallback<SessionId> OnOpenSession, OnFocusSession, OnRequestMessages;
EventCallback<LocatedShell> OnFocusShell, OnOpenShell, OnShellObserved, OnShellRemoved;
EventCallback OnRefresh, OnClose;
```

- Focus keys are `activity-subagents`, `activity-shells`, and `activity-shell-output`. Root applies the requested key after render and restores prompt focus on close. Push/use the existing composer keymap mode while open.
- Open Session is actual navigation. Row focus callbacks report selection only; they should not start execution or silently navigate.
- Interrupt calls existing `Client.InterruptAsync` for an authoritatively running Session. Shell kill calls `Client.RemoveShellAsync` at that row's original execution Location and reports the acknowledged removal. It does not create a “killed” snapshot locally.
- Merge `OnShellObserved` directly into root state. Do not synchronously reenter the reader from that callback. Merge `OnShellRemoved` by ID plus Location; that API operation removes the output file too.
- Root's existing SSE consumer should update the supplied snapshots. After a shell status/removal event, call public `SessionActivities.RefreshOutputAsync()` if an output reader is open. No second event stream is created.
- `OnRefresh` lets the root refresh its existing Session/list/active/shell projections. `OnRequestMessages` uses the existing Client messages API when details are explicitly requested; the pane does not create an independent message cache or poll every child.

## Output and wait behavior

Enter/click on a shell opens `ShellOutputPane`, an inline bounded reader using real GetShellAsync/ReadShellOutputAsync. It keeps four display pages of at most 65,536 requested bytes each. It advances using the server's returned **byte cursor**, never decoded string length. Its byte-range footer distinguishes locally omitted display pages from the protocol's truncation flag. Reset starts at byte zero; missing capture/status is an error, not empty success.

Public `ShellOutputPane.WaitForExitAsync(ct)` and W wait use authoritative shared-feed snapshots, with real GET refreshes before/after waiting. There is **no shell.wait HTTP route**, timer/process polling, sleep command, or extra SSE connection. Root must keep passing terminal observations or call RefreshOutputAsync on its existing feed. Closing the reader cancels only reads/local waits, never the shell process. Kill is a separate explicit action.

## Source interaction and semantics

- Up on the first row closes the composer; Down wraps. Left/right switch tabs. Escape/Ctrl+C close the composer. Subagent Enter/click navigates; pointer hover only selects. Ctrl+A toggles active/inactive children, resetting selection as source does.
- Source command IDs and defaults come from `config/keybind.ts`: composer.shell.up/down/kill and composer.subagent.up/down/select/interrupt; kill/interrupt default Ctrl+D. Pass `ResolveCommand`, `KillShortcut`, and `InterruptShortcut` to honor root bindings. A supplied resolver is authoritative; local fallback does not override disabled commands.
- Shell completed filtering, output viewing/reset/wait, child message details (Space), optional Job controls, and manual Ctrl+R refresh are explicit extensions requested for this port. Source list geometry remains five rows; details/output have separate bounded height.
- Agent identity uses `SessionInfo.Agent` only, with a generic “Subagent” role label when absent. No `@name subagent` title parsing is used to invent an agent. Titles use the existing timestamped Session title formatter.
- Completed/failed/interrupted Session labels come from the real terminal outcome only when not running. Idle without an outcome is Idle, not Completed. Shells show actual running/exited/timeout/killed states and real exit codes, not guessed Job outcomes.

## Root `!` submission

Keep command-input mode parsing/marks with the root owner. After the explicit user shell action has selected the Session and removed only the UI `!` marker, call the existing:

```csharp
await client.RunSessionShellAsync(sessionID, command, eventId, cancellationToken);
```

Do not call CreateShellAsync to synthesize a Session history message, prepend an agent command, or execute a local process. Server owns Started/Ended/admit-only completion even when the HTTP waiter disconnects. Merge its actual shell/Session events into the existing root projections; the new panes consume those fields.

## Verification boundary

Only the pinned local .NET 11 full CLI build, with isolated `C:\tmp\opencode\mcp-finish-pass` artifacts, is used. No tests, TUI/app, shell/job/Session execution, HTTP/SSE/DB/clipboard/process interaction, native probe, screenshot, or runtime layout exercise is performed. Core Jobs/Shell and storage files remain unchanged in this pass.

Final full CLI dependency-graph build succeeded with **0 errors**. Two existing CS0649 warnings were reported in `OpenTui.Blazor/Nodes/TuiNode.cs` (`InputMapText`, `InputMapWidthMethod`), outside this ownership. No Activities warnings were reported. This is compile verification only; focus/layout, shared-feed waiting, and actions were not run.
