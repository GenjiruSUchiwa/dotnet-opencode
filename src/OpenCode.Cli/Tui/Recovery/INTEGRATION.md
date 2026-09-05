# Recovery surfaces: root handoff

The implementation is in this subtree and `Components/OpenCodeApp.Recovery.cs`.
Root markup/keybindings remain owned by the main UI worker. No second error registry,
Session store, SSE subscription, service process, or directory mutation is created.

## Bind the actual receiver

The existing `SessionClientAdapter` supplies:

```csharp
ReadRecoveryFeed = () => adapter?.Feed;
RetryRecoveryFeed = ct => adapter.RetryConnectionAsync(ct);
ReloadRecoverySession = (session, ct) => adapter.ReloadSessionAsync(session, ct);
```

Use the same adapter instance already bound to prompts and `ReadSessionObservation`.
`RetryConnectionAsync` wakes its pending backoff and joins its next connection; it
does not create a parallel subscription or restart a daemon. The feed snapshot now
includes the real reconnect attempt and `RetryAt` timestamp. `ReloadSessionAsync`
rehydrates the same Session entry and retries its actual readiness callback. It
does not replace the selected Session or discard unconfirmed input IDs.

No connection snapshot is fabricated when the callback is absent. If the host has
not constructed an adapter yet, it may report its actual startup/connection state;
do not infer a managed restart merely because the endpoint is local.

## Reconnecting overlay

Add `@using OpenCode.Cli.Tui.Recovery`. At the end of the full-screen root Box:

```razor
@if (_recoveryFeed is { } feed)
{
    <Reconnecting Feed="feed" Theme="RecoveryColors"
                  TerminalWidth="_width" TerminalHeight="_height" />
}
```

The component implements the source timing from `app.tsx:1232–1255`: initial
connecting has a 5-second grace period, reconnecting has 1 second. Changing attempt
or backoff timestamps does not reset that timer. Live connection hides the overlay
immediately. Hydration remains a separate typed state, not a false connected/ready
claim.

Source geometry from `component/reconnecting.tsx` is retained: full-screen absolute
overlay, z-index 10000, RGBA(0,0,0,150), centered panel width 48 capped at 90% of terminal
width, one-cell top/bottom padding, two-cell horizontal padding and one-cell gap.
Native text metrics determine wrapped height. The source dot spinner advances every
80 ms. The default text is **Connection lost…** / **Reconnecting to the server
automatically.**

Like the source component, it has no independent keyboard focus, Escape dismissal,
or buttons that alter the panel geometry. It intercepts pointer actions over the
overlay without resetting the underlying editor. `ManagedRestart` is available only
for a host that can prove a real managed restart is in progress. This adapter cannot
restart a service, so the production handoff does not set it or advertise that action.

## Location-unavailable composer

In the Session composer switch, place this **after permissions and forms**, before
the normal prompt (also respect the root's existing expanded/child composer rules):

```razor
else if (_recoveryLocation is { } unavailable)
{
    <SessionLocationUnavailable Evidence="unavailable" Theme="RecoveryColors"
        Width="ComposerWidth" TerminalWidth="_width" Home="@RecoveryHome"
        Busy="RecoveryBusy"
        ChooseDirectory="@(ChooseRecoveryDirectory is null ? null : MoveRecoveryLocation)" />
}
```

The source `SessionQuestion` shape is retained: heavy left border, warning/title
header, max height 15, directory/body padding, raised action footer, horizontal
footer at width >=80 and vertical footer below 80. Directory display abbreviates
the supplied home and truncates the middle at 72 UTF-16 characters. A real move
callback exposes the single **Choose directory** action and Enter confirmation.
There is no Escape rejection or fullscreen toggle in the source location-recovery
question, so none is invented here.

### Evidence and capability limits

`LocationRecoveryEvidence.From` accepts only a real HTTP 503 with the canonical
`ServiceUnavailableError` whose `Service` equals `location`, associated with the
captured Session and Location. It never parses English exception text, checks local
directory existence, or interprets generic errors as a missing directory. Evidence
is retained on the existing observer snapshot, and root display requires an exact
Session/directory/workspace match.

The current server does **not** distinguish a physically missing directory from
other Location service failures. This UI therefore says **location unavailable**,
not that a directory was proven missing. A generic provider, permission, filesystem
or transport failure does not activate this card.

The parent has now supplied the typed Session-move API. `DIRECTORY-MOVE.md` describes
the real picker, server-backed worktree/filesystem reads, move admission callback,
and root mount. Without those callbacks the card still reports the capability gap.
HTTP acceptance never changes `CurrentDirectory` locally or clears unavailable
evidence. Only successful origin-scoped readiness or authoritative Location metadata
retires the corresponding evidence; an unconfirmed move is never automatically retried.

## Reload/cancel controls and focus

Use the separate inline status view in the root's existing feedback slot, not inside
the source-sized reconnect panel:

```razor
@if (RecoveryDataUnavailable || RecoveryBusy || _recoveryWaitError is not null)
{
    <RecoveryStatus Theme="RecoveryColors"
        Synchronization="@(_recoverySession?.Synchronization ?? SessionSynchronization.Empty)"
        Error="@(_recoveryWaitError ?? _recoverySession?.Error)" Busy="RecoveryBusy"
        Retry="@(RetryRecoveryFeed is null ? null : RetryRecoveryConnection)"
        Reload="@(ReloadRecoverySession is null || _sessionId is null ? null : ReloadSelectedRecoverySession)"
        Cancel="CancelRecoveryWait" />
}
```

Root command registration may expose **Retry connection now**, **Reload session
data**, and **Cancel recovery wait** through these same handlers. No default key
binding is guessed. Cancel only cancels the explicit caller's wait. Automatic
reconnect, retained observers, server execution, transcript, drafts and per-item
admission uncertainty survive. Changing Session or Location cancels the old UI wait
and prevents a late error from appearing in the new context.

`ReadRecoveryState()` is already called by the owned tab/frame helper.
`StopRecoveryAsync()` is already joined by `StopTabActionsAsync()`. The main root
must retain its existing `StopTabActionsAsync()` shutdown call; no additional
receiver or lifecycle loop needs to be mounted.

## Verification

Pinned .NET 11 isolated CLI compilation only. No recovery component, native renderer,
SSE/API request, directory lookup/move, service process, database, screenshot or test
was run. These views are not marked mounted until the main root adds the markup above.
