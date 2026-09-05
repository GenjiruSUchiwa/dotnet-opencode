# Directory-switch recovery

The move flow now uses the parent's canonical
`SessionHttpClient.MoveAsync(SessionId, LocationRef, InboxDeliveryMode?, ct)`.
No raw HTTP duplicate, local filesystem probe, client-side placement change, or
invented control ID is used.

## Same-observer adapter API

```csharp
Task<SessionMoveSnapshot> MoveSessionAsync(SessionId session, LocationRef destination,
    InboxDeliveryMode? delivery = null, CancellationToken ct = default);
SessionMoveSnapshot? ReadMove(SessionId session);
```

`SessionObservationSnapshot.Move` exposes the same record. Its phases are:

- `Submitting`: the actual request is outstanding.
- `Admitted`: HTTP 204 acknowledged the move request. This does not prove delivery.
- `Unconfirmed`: transport/5xx/cancellation after submission left admission unknown.
- `Rejected`: the request was rejected, or cancelled before submission.
- `LocationChanged`: authoritative Session hydration observed a different Location.

The record retains the original and requested Locations, delivery mode, error and
observed Location. A location change can come from another client; without a caller
control ID it is not an exactly-once or correlated delivery receipt. The UI reports
the actual observed location rather than claiming the requested control completed.

The existing Session admission semaphore serializes the POST with prompt/command
admissions. It is released after the POST, not after model idle. Only the shared
observer refreshes afterwards. There is no automatic move POST retry, including
after reconnect. A pending/admitted/unconfirmed move prevents accidental reposting
until an authoritative location change is seen. An explicit reload only reads;
it does not resend the move. Cancellation of the dialog can leave an unconfirmed
move, which stays on the observer.

Origin and destination explicit workspaces are rejected explicitly, matching the
current local movement implementation. The server validates and resolves manual
paths. The client does not expand home, normalize relative paths against its own
working directory, stat folders, copy files, or create a worktree implicitly.

## Real destination discovery

Construct `RecoveryDirectoryClient` with the existing authenticated client resolver:

```csharp
var directories = new RecoveryDirectoryClient(RequireApi);
```

- `WorktreesAsync(project, current, ct)` calls typed worktree refresh/list APIs and
  preserves actual server paths. Current-containing roots sort first, then ordinary
  roots and worktrees as in the source picker.
- `ChildrenAsync(location, ct)` calls typed `ListFilesAsync`, filters actual directory
  entries and resolves their display paths lexically against the **server-returned**
  Location. It never asks the local filesystem.

An initial discovery failure stays an error, not an empty-list success. Refresh
failure retains an already loaded list. A genuinely empty server list says no
worktrees are available. Manual entry remains an explicit separate mode and is
validated by the move endpoint. Filtering text alone never becomes a move path.
Worktree creation/deletion are not advertised by this recovery-only picker.

## Root-owner binding and mount

Bind these existing helper parameters:

```csharp
LoadRecoveryDirectories = directories.WorktreesAsync;
BrowseRecoveryDirectory = directories.ChildrenAsync;
SubmitRecoveryMove = (id, destination, delivery, ct) =>
    adapter.MoveSessionAsync(id, destination, delivery, ct);
```

In the mounted location-unavailable card, replace the prior callback-presence test:

```razor
ChooseDirectory="@(CanChooseRecoveryDirectory ? MoveRecoveryLocation : null)"
Move="RecoveryMove"
```

`MoveRecoveryLocation` now opens the actual picker when `SubmitRecoveryMove` is
connected. It retains the older host-provided `ChooseRecoveryDirectory` callback
as an optional compatibility path. Do not supply a no-op callback.

Alongside other root modal mounts:

```razor
@if (_recoveryMovePicker is { } moveSession)
{
    <DirectoryMoveDialog @key="moveSession.Id" SessionId="moveSession.Id"
        ProjectId="moveSession.ProjectId" Current="moveSession.Location"
        Theme="RecoveryColors" TerminalHeight="_height"
        LoadDirectories="LoadRecoveryDirectories" LoadChildren="BrowseRecoveryDirectory"
        SubmitMove="SubmitRecoveryMove" MoveState="PickerMove"
        OnAdmitted="RecoveryMoveAdmitted" OnClose="CloseRecoveryDirectory" />
}
```

Include `CloseRecoveryDirectory()` in the root's close-all-dialogs path. The picker
uses the source extra-large modal size, current/other grouping, keyboard and pointer
selection, typed text/paste input, server refresh, and explicit browsing/manual modes.
The existing generic modal owns focus/Escape restoration.

After acceptance the dialog can close, but the unavailable composer remains. Its
pending message says **Move admitted; waiting for an authoritative location change**.
It does not set `CurrentDirectory` or clear evidence from the HTTP return value.
`ReadRecoveryState` follows the existing observer's authoritative Session metadata.
Unconfirmed moves remain visible and cannot be blindly resubmitted from the picker.

The 503 `service: location` evidence still means unavailable, not proof that a
physical folder is missing. Moving is a recovery option, not a filesystem diagnosis.

Verification remains pinned .NET 11 isolated CLI builds only. No dialog, API, VCS,
filesystem, database, SSE, application, process or test was executed.
