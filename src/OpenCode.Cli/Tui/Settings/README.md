# Native settings integration

## Root-owner handoff

Share one store/controller across the app, palette, and theme commands:

```csharp
var store = new CliSettingsStore();
var settings = new CliSettingsController(store);
SettingsRuntimeBindings.RegisterInteraction(settings, host.Interaction);
await using var themePersistence = new ThemeSettingsPersistence(
    settings, store, themeCatalog, themeState, ReportError);
```

After the actual keymap dispatcher exists, register it:

```csharp
SettingsRuntimeBindings.RegisterKeymap(settings, dispatcher, ClearLeaderHint);
```

For supported session presentation features, register the actual state setter,
not an empty callback. For example, `settings.Register(CliSettings.Thinking,
value => SetThinkingVisible(value == "show"))`. Optional definitions also cover
`session.sidebar`, `session.markdown`, and `session.grouping`. Register those only
when the mounted component consumes the value. Registration requires a typed
consumer; duplicate IDs are rejected. There is no broad catalog of pretend
handlers. `available` can reflect actual consumer availability.

Call `await settings.LoadAsync(cancellationToken)` on the UI dispatcher after
registration to load and apply existing preferences. Reading creates no files
and performs no migration. If the root does not preload, the Settings dialog
loads once on mount and shows load failures without overwriting the file.

Mount the actual screen in the app-owned dialog branch:

```razor
<DialogConfig Controller="Settings" Theme="ResolvedDialogTheme"
              Current="@SelectedSettingId" TerminalHeight="_height"
              OnClose="CloseDialog" />
```

Feed `Settings.PaletteSettings` into `CommandPaletteDialog.Settings`. Its
`OnSetting` receives the same setting ID. Replace the palette with this settings
screen, using that ID for `Current`. Register the actual open-settings command
in the root registry; do not add a default binding as a fake implementation.

The source screen cycles in place: Enter/Right advances, Left reverses. Choice
values wrap, numeric values clamp at the source min/max, and current values are
shown as row footers. Search, grouping, current selection, and geometry use the
shared source-based `DialogSelect`. Filtering notifies the settings controller's
selection path, so Left/Right changes the filtered selection rather than a stale
hidden row. The footer is the source `←/→ change` hint.

## Theme wiring

`ThemeSettingsPersistence` uses the theme owner's `ThemeCatalog`, `ThemeState`,
and `CliThemeSettings.FromConfig`; it does not add another resolver or palette.

- Theme names come from the real catalog in case-insensitive order.
- Settings changes validate through that resolver, save, then call
  `ThemeState.ApplyConfig`, which does not emit another persistence request.
- Existing theme commands can continue calling `ThemeState.Set`, `SetMode`, or
  `Unlock`. `PersistenceRequested` queues a single atomic edit of both
  `theme.name` and `theme.mode` through the same writer.
- The error callback is required. A failed event-driven save is reported as a
  locally changed theme whose preference could not be saved; it is not silently
  called persisted.
- Retain the adapter for the app lifetime and await `DisposeAsync` or
  `FlushAsync` at shutdown. Do not also attach a second config-writing listener.

All dialog colors come from the caller's resolved `DialogTheme` semantic roles.
This subtree contains no new color palette or theme assets.

## Supported source metadata

The available entries depend on actual registrations:

| ID | Default | Source interaction |
| --- | --- | --- |
| `theme.name` | selected theme / `opencode` | Cycle installed catalog names |
| `theme.mode` | `system` | system, dark, light |
| `session.sidebar` | `auto` | hide, auto |
| `session.thinking` | `hide` | hide, show |
| `session.markdown` | `rendered` | source, rendered |
| `session.grouping` | `auto` | none, auto |
| `scroll.speed` | 3 | 0.25 steps, range 0.25–10; two decimals |
| `mouse` | true | off, on |
| `leader.timeout` | 2000 | 250 ms steps, range 250–10000 ms |

The native host consumes `TerminalInteractionOptions` live. Fractional wheel
steps accumulate rather than rounding every event to zero. The keymap binding
updates the existing dispatcher's leader timeout and clears pending input; the
optional root callback clears its displayed leader hint.

The source `dialog-config.tsx` does not open separate numeric/choice dialogs or
offer a reset button, so none is invented here. `ResetAsync(id)` is an explicit
controller API for a separately registered reset action: it removes only that
override and applies the source default. No reset action is registered by this
subtree.

## Persistence

`CliSettingsStore` uses shared global `cli.json`: `OPENCODE_CONFIG_DIR`, otherwise
`$XDG_CONFIG_HOME/opencode`, otherwise `~/.config/opencode`. It does not write
project configuration, migrate legacy documents, or copy preferences to a
dotnet-specific file. An explicit file path is supported for caller-owned
configuration contexts.

On a user edit, the writer rereads the latest document, uses token-derived
UTF-16 spans to replace only the selected value or insert/remove the selected
property, validates the resulting JSONC, and replaces the file from a unique
same-directory temporary file. It preserves unrelated keys, ordering, comments,
trailing commas, newline style, and UTF-8 BOMs. Newly inserted properties use the
source two-space indentation convention. It does not regex-match objects or
serialize the entire parsed document back to disk. Duplicate target keys,
malformed JSONC, or non-object intermediate paths fail without replacing the
configuration. Reset does not prune unrelated parent objects.

Writes are serialized within the store, and a changed-content check rejects an
external edit observed before replacement. This is not a claim of shared
cross-process flock compatibility or race-free coordination with every editor.
Unix temporary-file permissions use an actual supported-platform guard; no
platform analyzer warning is suppressed.

Sources: `packages/tui/src/component/dialog-config.tsx`,
`packages/tui/src/config/index.tsx`, `packages/cli/src/config/config.ts`,
`packages/cli/src/config/schema.ts`, and `packages/util/src/global.ts`.

Validation for this pass was a full pinned .NET 11 CLI dependency build. No
tests, runtime/native launches, or user-config read/write verification were run.
