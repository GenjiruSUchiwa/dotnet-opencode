# Model preferences and picker integration

Owned implementation: `Tui/Models` and `Dialogs/ModelPicker.razor[.cs]`.
Root markup, `ChooseModel`, keymap layers, and integration dialogs remain with their owners.
Root mounting and preference/cycling integration are now owned by the main root implementation.
The remaining focused-action keymap hook below must be connected by that owner.

## Root bindings

Keep one `ModelPreferenceService` for the client lifetime. Call `LoadAsync` during client
initialization, display load failures, and retain the last successful `Value`. Construction
does no I/O. The picker can load the service if it was not loaded by the host.

Add these parameters to the existing `ModelPicker` mount, using the root's actual field names:

| Parameter | Contract |
| --- | --- |
| `Preferences` | The shared `ModelPreferenceService`, also used by cycling. |
| `Integrations` | Actual current integration catalog; connected means at least one `Connections` entry. Null is unknown, not connected. |
| `Keybindings` | The parsed `TuiKeybindConfig`. Never construct an independent default config when the host has overrides. |
| `Shortcut` | Existing configured shortcut formatter, keyed by source command ID. |
| `ResolveCommand` | Existing dialog key resolver. Include `model.dialog.favorite` as well as `model.dialog.provider`; dispatch configured multi-stroke/leader bindings through the root keymap. |
| `RegisterActionDispatcher` | `Func<Func<string, Task<bool>>, IDisposable>`; register the supplied focused-action delegate in the existing root keymap and return the registration lease. See below. |
| `AcceptSelection` | Optional `Func<ModelRef, CancellationToken, Task>` that accepts this exact choice, or throws. Must not close the picker or select a fallback. |
| `OnSelect` | Existing `EventCallback<ModelRef>` fallback when `AcceptSelection` is absent. Successful completion means acceptance; swallowed failures are not supported. |
| `ConnectProvider` | Optional `Func<ProviderId?, CancellationToken, Task<ProviderId?>>`: open the real integration flow, refresh the real catalog, and return the connected provider ID. Return null for cancellation. |
| `UnavailableReason` | Optional authoritative catalog/transport availability check. Default checks enabled policy and `AppCatalog.SupportsTransport`. Do not infer authentication from the mere presence of model metadata. |

`Current`, `Actions`, `PreferredVariant`, `Theme`, `Size`, `Loading`, and `Error` remain supported.
Existing provider/favorite entries in `Actions` take precedence over built-in callbacks. A
custom favorite action must use the same preference service. If the root already replaces
the model dialog with an integration dialog, retain that real action and remount the picker
with `Provider` set after connection; do not also run `ConnectProvider`.

The picker records recents only after the acceptance callback succeeds. It selects the base
model before opening the variant stage, as upstream does. Cancelling variants leaves that
accepted model selected. A valid current/retained variant skips the variant stage.
`OnAccepted` is an optional typed notification after acceptance **and** successful persistence;
it is not another selection or persistence hook. Do not write the same preference twice.

An accepted choice can outlive the picker. Preference writes therefore finish independently
of dialog cancellation. A write failure leaves a visible error and a local retry action that
retries only persistence, never model selection. `model.preferences.retry` is a private
dialog action identifier, not a configurable source keybind. It is reachable by pointer or
Tab/Enter. Closing does not undo a selection that already succeeded.

## Focused-action keymap hook

Add `RegisterActionDispatcher="RegisterModelActionDispatcher"` to the existing root
`ModelPicker` mount. This is a command invocation hook, not another keyboard dispatcher.
The picker passes it only to the model-list stage, never the variant stage.

The callback receives `Func<string, Task<bool>> dispatch`. It calls the mounted
`DialogSelect.DispatchActionAsync(command)` on the renderer dispatcher. That method looks
up the latest action and uses the selector's current focused option, including keyboard,
pointer, filter, and refreshed-option changes. It does **not** use the `Current` marker or
call `OnMove`, `OnSelect`, or accepted-choice persistence. Favorite invokes the existing
favorite action; Provider invokes the existing integration action. Supplied `Actions`
retain their existing precedence. Disabled, locked, or busy actions do not execute, and
action failures remain visible through the existing selector error path.

`true` means the command belongs to this dialog, even if it is currently disabled/busy.
`false` means absent action, disposed selector, or no longer the model-list stage. Do not
interpret the result as acceptance of a model choice or retry a false result on `Current`.

Root example using its existing configuration, registry, and owned task collection:

```csharp
private IDisposable RegisterModelActionDispatcher(Func<string, Task<bool>> dispatch)
{
    var config = _resolvedBindings ?? throw new InvalidOperationException("Keymap is not initialized.");
    var commands = new[] { ModelPreferenceCommands.Provider, ModelPreferenceCommands.Favorite }
        .Select(id => new TuiKeymapCommand(new KeymapCommand(id, _ =>
        {
            _keyTasks.Add(dispatch(id));
            return true;
        }))).ToArray();
    var layer = TuiKeymapLayer.Create(config, commands, mode: TuiKeymapLayer.ModalMode,
        condition: new() { When = _ => _models });
    return _keyLayers.Register(layer with
    {
        Bindings = layer.Bindings.Where(binding => binding.Sequence.Count > 1).ToArray()
    });
}
```

This registers only configured leader/multi-stroke sequences. Disabled bindings remain
disabled; single-stroke bindings continue through the existing `ResolveDialogCommand` path.
Do not also deliver a consumed sequence's final key to that path. The root's current
dispatcher remains responsible for pending-prefix capture, cancellation, native-event
handling, and configured prevent-default behavior. No new config/defaults are constructed.
The root must observe/drain `_keyTasks` through its existing task handling, not block on them.

The selector registers after its first render and disposes the returned lease when its hook
changes or it unmounts. Closing, provider-stage replacement, switching to variants, and root
shutdown therefore unregister the exact old selector's layer. Keep the registration callback
stable across ordinary renders; do not add the returned lease to an independently reset global
lease collection. If the root replaces its keymap registry/configuration, replace the callback
delegate too so the old lease is disposed and a new one registered. Scope the layer to the
model modal (as above), so a root close is inactive even before the unmount render completes.
Layer removal must use the root's existing pending-keymap cleanup if needed; do not create a
second leader tracker. Stale delegates are harmless after selector disposal and return false.

## Cycling handlers

Create `ModelSelectionController(sharedPreferences, acceptExactSelection)` for root commands.
Its callback applies the actual selection but does not record preferences; the controller
records them after acceptance. Pass the same optional availability check used by the picker.
Use `CycleAsync(Current, Models, Providers, action, direction, cancellationToken)`:

| Source command ID | Action | Direction |
| --- | --- | --- |
| `model.cycle_recent` | `RecentCycle` | 1 |
| `model.cycle_recent_reverse` | `RecentCycle` | -1 |
| `model.cycle_favorite` | `FavoriteCycle` | 1 |
| `model.cycle_favorite_reverse` | `FavoriteCycle` | -1 |
| `variant.cycle` | `VariantCycle` | 1 |

Gate recent/variant commands when there is no current model. Favorite cycling may start
without a current model. No available favorite produces the source informational message.
Recent cycling prepends the current model for navigation, deduplicates, takes ten, and filters
by catalog presence. It does not reorder persisted recents. Favorite cycling records the
accepted candidate as recent. Variant cycling includes the default slot and writes only the
chosen model's variant preference. A model with no variants is unchanged.

Use `SelectAsync(new(model, ModelPreferenceAction.VariantPicker), ...)` for a separately
mounted root variant dialog. Do not use this persistence controller for hydration, restored
session metadata, hover, or automatic fallback selection. Use the underlying selection
operation for those paths. `ModelPreferencePersistenceException.Selection` means the
selection succeeded but saving did not; report this without automatically reapplying it.

These are five specific cycling handlers, not an implementation claim for the complete
configured keybind catalog. Source IDs/defaults were read from `packages/tui/src/config/keybind.ts`.

## Persistence and source behavior

- File: `$XDG_STATE_HOME/opencode/dotnet/model.json`, or
  `~/.local/state/opencode/dotnet/model.json`. No production-channel file is read or overwritten.
- Source document fields: `recent`, `favorite`, and `variant`; model entries use `providerID`
  and `modelID`, and variant keys are `providerID/modelID`.
- Locked read-modify-write and atomic replacement follow the existing dotnet client-state
  convention. Unknown top-level fields, unavailable favorites, and other models' variants
  survive updates. Malformed containers fail rather than being overwritten.
- No catalog refresh or hydration writes preferences. Missing models disappear from visible
  sections/cycle candidates, not storage. New favorites are prepended; recents are capped at ten.
- Empty search shows Favorites, Recent excluding favorites, then remaining provider groups.
  `opencode` sorts first, followed by provider name, descending release date, and model name.
  Search uses the existing fuzzysort port on title/category and a favorite-priority snapshot
  captured at dialog entry. Provider filtering hides the extra sections.
- Unavailable rows remain visible with their reason. Selecting one fails truthfully; no other
  provider/model is substituted. Authentication failures must propagate from the real owner.
- Variant preference `default` normalizes to no override. Unavailable stored variants are
  retained on disk but are not applied. The variant picker lists actual catalog variants;
  it does not invent an extra default entry.

## Shared-selector focus

The themed shared selector retains existing geometry. `DialogSelect.FocusCurrent` defaults
to true, preserving other pickers' behavior. The model picker sets it false while retaining
`HasCurrent` and `Current` for the marker. Current-model changes do not steal focus. Like
upstream, the model picker leaves `PreserveSelection` false: option updates retain/clamp the
cursor index rather than following the previously focused model by identity. Nonempty filter
changes start at the first result; clearing the filter does not jump to the current model.
The picker keys its variant/provider stages to reset search state on replacement.

## Verification boundary

Only the repository-pinned .NET 11 isolated full CLI build is permitted for this batch.
No app, tests, preference I/O, provider/API calls, filesystem listings, or screenshots were
run for verification. See the task report for the latest build result; compilation does not
establish runtime or visual parity.
