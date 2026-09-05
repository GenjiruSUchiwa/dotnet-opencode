# Application theme resolver

This subtree ports the semantic theme pipeline, not a replacement visual design.
No generic Blazor component or native binding depends on these OpenCode types.
All 33 source built-in theme assets are embedded byte-for-byte through one
`Tui/Theme/Assets/*.json` resource glob in the CLI project. There is no Bun/Node
build or runtime dependency. `Assets/catalog-provenance.json` embeds the source
paths, exact SHA-256 fingerprints, registry order, and upstream MIT license.
The provenance resource is metadata, not an installed theme.

## Root UI owner handoff

### Mounted component bindings

`Components/OpenCodeApp.Theme.cs` now supplies `ComposerColors` and
`WordmarkColors`, alongside the existing dialog/transcript/form/tab records.
The root owner must add these bindings to its existing markup:

```razor
<Wordmark TerminalWidth="_width" Theme="WordmarkColors" />
<Composer ... Theme="ComposerColors" />
```

Do not create replacement views. These parameters belong to the existing
production components. The optional component fallback resolves the real
opencode default only for older callers; a configured root must pass its records.
Wordmark rows rebuild when either layout mode or semantic colors change. Shadow
colors use the source quarter tint of the current background and each foreground.
The source-approved bold `#7B61FF` dotnet label is the only logo color constant.
Composer controls use formfield/action roles; selected variant metadata is not
presented as a warning. Both opaque and transparent prompt bottom borders follow
the resolved prompt surface. No terminal Dim shortcut substitutes for subdued
theme text.

The dialog adapter now uses the elevated context, matching both source Dialog
and DialogSelect. In root-owned direct Modal branches, use
`Background="@DialogColors.Background"` and explicit `Fg="@DialogColors.Text"`.
Root-owned Home footer/status/version text should use
`Fg="@BaseColors.Subdued.Hex"`, not default foreground plus Dim.

### Shared settings adapter lifecycle

The root passes the existing app-wide store/controller/catalog through one
factory; the Theme partial creates the actual shared adapter on initialization:

```csharp
[nameof(OpenCodeApp.CreateThemePersistence)] =
    (Func<ThemeState, Action<Exception>, ThemeSettingsPersistence>)((state, report) =>
        new ThemeSettingsPersistence(settings, store, themeCatalog, state, report))
```

`ConnectThemes` is idempotent for that ThemeState. It retains the adapter and
does not also attach the legacy `PersistThemeSettings` callback. Do not construct
a second ThemeSettingsPersistence elsewhere when supplying this factory.
`DisconnectThemes` removes the UI listeners, disposes the owned adapter, and
joins its flush into `_themeUpdate`, already awaited by root StopAsync. It does
not dispose the caller's ThemeState, catalog, store, or controller. The theme
state/controller registrations have app lifetime; replacing them requires a
remount rather than duplicate settings registrations.

Theme changes and asynchronous save errors are serialized through InvokeAsync;
the existing root OnFrame batches StateHasChanged. The helper never evaluates
configuration or writes files itself. The controller still needs its existing
LoadAsync call after all real registrations, on the UI dispatcher.

The legacy callback parameter/methods remain available when no factory is
provided. Existing private color/lifecycle method names were preserved for the
root owner's partial-class code.

### Generic host capability requests

At source inspection, OpenTuiHost still clears its buffer to fixed RGB(10,10,10).
The generic owner must expose an application-supplied NativeRgba clear-color
provider; root should supply `() => themes.Current.Base.Background.Native`.
The generic Input also needs cursor/selection color parameters to consume the
remaining formfield states. Neither generic file was changed here. Root markup,
shared-writer factory mounting, and those host capabilities must be confirmed by
their owners before claiming complete mounted theme parity.

```csharp
var settings = await CliThemeSettings.ReadAsync(configDirectory, cancellationToken);
var catalog = new ThemeCatalog();
catalog.SetCustom(await ThemeDiscovery.DiscoverAsync(
    ThemeDiscovery.ConfigDirectories(configDirectory, launchDirectory), cancellationToken));
// If already available from the native owner:
// catalog.SetSystem(SystemTheme.Generate(palette, settings.Mode == ThemeModePreference.Light
//     ? ThemeMode.Light : SystemTheme.DetectMode(palette) ?? ThemeMode.Dark));
var themes = new ThemeState(catalog, settings, terminalMode);
themes.MarkReady();
// Marshal events and mutations through the renderer's existing dispatcher.
themes.Changed += Invalidate;
themes.PersistenceRequested += SaveThroughExistingCliConfigService;
var normal = themes.View();
var dialog = themes.View(ThemeContext.Overlay);
var nativeBackground = dialog.Background.Native;
var nativeText = dialog.FormfieldText(ThemeActionState.Focused).Native;
```

The example is integration guidance; it has not been executed. The root owner
must dispose the ThemeState and unsubscribe its own delegates. Reacquire a view
on each render after Changed, rather than retaining a stale token snapshot.
Apply native background changes and syntax-style replacement in the renderer
owner, not this resolver. UI/screenshot parity remains unverified.

`ThemeTokens.Color(path)` exposes every source semantic leaf, with resolved
state names (`focused`, not `$focused`). Action and formfield helpers provide
typed access. For example `text.status.running`, `text.feedback.warning.default`,
`background.surface.offset`, `diff.lineNumber.background.added`,
`syntax.keyword`, and `markdown.heading` are distinct roles.

`ThemeDefaults.Document()` is the exact generic V2 fallback document.
The catalog's `opencode` is instead the actual production V1 palette translated
by `LegacyThemeMigration`, including its OKLCH-derived hues. These must not be
interchanged. The source default palette is not simply the generic blue theme.

`ThemeCatalog.BuiltinNames` exposes the complete source registry in insertion
order. `catalog.List()` gives picker entries sorted with base-sensitivity
linguistic comparison; `Name` and `Label` both use the exact source name.
`catalog.Describe(name).Modes` comes from V1 migration/V2 resolution, including
single-mode collapse. It never assumes every asset supports both modes.
`ThemeCatalog.BuiltinProvenance()` exposes the embedded provenance/license data.

### Transcript owner: native-ready syntax data

```csharp
var rules = ThemeSyntax.GenerateNative(themes.View());
// Preserve declaration order and scope groups when installing in your parser.
// Given a selected rule and already encoded UTF-8 text:
NativeTextRun run = rule.ToRun(utf8Text);
```

Each `NativeThemeSyntaxRule` contains `Scopes`, RGB-intent `NativeRgba`
foreground, optional background, and the source native attribute mask
(bold=1, italic=4, underline=8). `ToRun` only creates managed run data. None of
these methods allocate native syntax handles or invoke native APIs.
The existing `ThemeSyntax.Generate` and all root-facing APIs remain unchanged.

## Implemented semantics

- V2 document validation, light/dark availability and selected-mode fallback.
- `mergeMode`, including missing-other-mode and both-merge rejection.
- Standalone red diagnostic fallback and normal default-token inheritance.
- Default/state expansion before merging; semantic/hue references, aliases,
  missing reference and cycle errors; elevated and overlay contextual actions.
- Hue aliases clone colors. Hue source/increase/decrease use reference identity;
  equal literal colors do not accidentally acquire a hue source. Raise follows
  the resolved light/dark mode.
- Hex RGB/RGBA short and long forms, transparency, ANSI indexed RGB conversion,
  source OKLCH conversion, gamut fitting, interpolation, and terminal palette
  mode detection/system-theme generation from caller-supplied snapshots.
- V1 defs/theme references, mode variants, inverse selection colors, neutral and
  chromatic hue inference, categorical ordering, diff/syntax/Markdown roles,
  and single-mode collapse for identical opaque backgrounds.
- Registry precedence: built-in < installed/plugin < custom < generated system.
- Global shared `cli.json` theme.name/theme.mode decoding; no project CLI config,
  no keybind-loader changes, no implicit config migration or writes.
- Ordered theme discovery from global/root-to-leaf .opencode directories; sorted
  .json files, later names replacing earlier ones. No recursive theme scanning.
- Application mode locks, selection, configuration updates, change notifications,
  and explicit persistence requests to the existing configuration writer.

## Native color boundary

OpenTUI 0.5.9 RGBA.ts and NativeRgba both use four uint16 lanes containing RGBA8
in their low bytes. RGB intent metadata is zero. ThemeColor.Native uses the
existing byte constructor; values must NOT be multiplied by 257. Keep ThemeColor
identity until the final native conversion if hue stepping is required.

## Explicit remaining integration/coverage

- All names from the inspected DEFAULT_THEMES registry are bundled. Arbitrary
  unknown configured names still fail explicitly instead of selecting a substitute.
- Terminal palette queries, detection retries, notification sequences, SIGUSR2,
  delayed refresh scheduling, renderer background application, and native syntax
  style lifetime remain root/native-owner integration. No native API is invoked
  by the resolver. A system-selected state requires a supplied system palette.
- Shared cli.json migration, atomic/comment-preserving writes, and watching stay
  with the configuration owner. ApplyTo returns a modified copy only.
- Invalid discovery/configuration/theme documents propagate errors. Unlike the
  source UI's silent discovery fallback, this port does not conceal a configured
  theme failure. ThemeState retains its previous tokens on a failed reload.
- Full V1 in-memory RGBA objects are not a JSON document format. System palettes
  are converted to exact hex values before migration. Noninteger ANSI values and
  invalid hex are rejected rather than synthesized.
- No tests or test sources, live config reads, terminal execution, screenshots,
  runtime/API/database checks, or application launches were used. Build success
  alone does not establish runtime or visual parity.

Source references: packages/theme/src/tui/{schema,defaults,expand,fallback,resolve,
color,select,types,v1-migrate}.ts; packages/tui/src/theme/{index,discovery,system,
component,color,v1}.ts; packages/tui/src/context/theme.tsx; the opencode theme
asset; packages/cli/src/config/config.ts; docs/tui-parity-scope.md.
