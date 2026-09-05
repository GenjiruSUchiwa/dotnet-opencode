# Skill catalog and picker handoff

## Server owner

Mount the new dedicated extension:

```csharp
app.MapSkillEndpoints();
```

**First remove/replace the existing `/skill` mapping inside `FeatureEndpoints.MapFeatureEndpoints`.** Both map the same canonical `GET /api/skill`; mounting both produces an ambiguous route. That existing owner file was not edited in this pass.

The endpoint returns the exact `Location.response(Skill.Info[])` contract from `protocol/src/groups/skill.ts`. It resolves request placement through the existing request/Location resolver, borrows the actual shared ToolLocationFactory/PermissionLocationMap lease, and calls `InstructionCatalog.ListSkillsAsync` for that Location. It does not start MCP connections or evaluate a skill tool.

The existing instruction producer owns all precedence/discovery: ambient `.claude`/`.agents`, global/project `.opencode` skill roots, configured roots, frontmatter, ID replacement, and source availability. The same API is already used by native prompt skill preparation. List is not agent-filtered, matching source Skill.list; guidance/manual execution own their separate permission semantics.

Configured or discovered plugin contributions and remote skill pull sources remain explicitly unsupported by that producer. The endpoint returns 503 rather than claiming the filesystem subset is a complete registered plugin catalog. Unavailable/read failures do not become successful empty lists. A genuinely complete empty catalog is allowed.

No Core/Skill, Core/Reference, or Core/Instructions implementation was changed or duplicated.

## Client and shared root cache

```csharp
Task<LocationResponse<IReadOnlyList<SkillInfo>>> ListSkillsAsync(
    string? directory = null, string? workspace = null, CancellationToken ct = default);
```

`SessionHttpClient.Skills.cs` uses the exact endpoint and existing authenticated transport. It preserves source errors and checks required catalog identity/content fields and duplicate IDs. IDs are branded strings, **not** necessarily `skl_`-prefixed. Server paths are metadata and are not interpreted as local client paths.

Root can retain the returned complete response in a `SkillCatalogSnapshot`. Scope caches by **Client plus canonical Location**. Pass an unavailable reason instead of stale/partial data when the root knows discovery failed.

## Picker mount

```razor
@using OpenCode.Cli.Tui.Skills

<SkillPicker @key="(client, location)" @ref="skillPicker"
    Client="client" Location="location"
    Theme="DialogColors" ErrorColor="@FormColors.Error"
    TerminalHeight="height" Catalog="cachedSkills"
    UnavailableReason="@skillCatalogError"
    OnLoaded="CacheSkills" OnSelect="SelectSkill" OnClose="CloseDialog"
    ResolveCommand="ResolveDialogCommand" />
```

`Catalog` is optional. If absent, the picker calls the actual Client list method. If supplied, it must belong to the same canonical Location. Changing Client/Location requires remounting with a new key. Root can call `RefreshAsync()` on the existing `skill.updated` feed; no duplicate SSE connection is created.

Callbacks:

```csharp
EventCallback<SkillCatalogSnapshot> OnLoaded;
EventCallback<SkillSelection> OnSelect;
EventCallback<SkillInfo> OnFocus; // optional metadata selection, not skill execution
EventCallback OnClose;
```

The picker uses the existing production DialogSelect at large size. Skill names retain catalog order and source padded-name presentation; descriptions collapse whitespace. Name, ID, and description are searchable. Loading and failure states do not offer selection; a failed load has a dedicated “Could not load skills” view rather than a fake empty catalog. Close/reopen retries as in source. Empty catalogs show “No skills available”. The shared selector's no-match wording remains its existing “No results found”; that owner's generic component was not edited.

There is no file-opening, arbitrary-path, skill-execution, or auto-submit action in this picker. Selecting a row returns a current catalog member and closes only after the caller's selection callback succeeds.

## Typed prompt/slash/mention integration

`SkillSelection` exposes the actual `SkillInfo` and catalog `Location`. Its constructor is internal; selection comes from `SkillCatalogSnapshot.Select(id)`, which rejects unknown IDs.

```csharp
PromptInputSkillAttachment attachment = selection.ToAttachment(actualMention);
// Append attachment to the root editor's PromptInput.Skills.
// Submit later through the existing client.PromptAsync(...) path.
```

`actualMention` is the root editor's real span/text, or null when there is no textual mention. Do not invent zero offsets, materialize `PromptSkillAttachment`/skill content on the client, convert the location to a file URI, or use an arbitrary string path as a skill ID fallback. Native SessionPromptPreparation rereads the registered catalog and resolves the ID; a skill removed since selection fails rather than loading an arbitrary file.

Root autocomplete can use:

```csharp
snapshot.Mentions(); // all actual skills, displayed as @id
snapshot.SlashCommands(actualServerCommandNames); // slash == true, excluding command-name collisions
```

Both return `SkillCompletion(Display, Description, SkillSelection)`. Server command-name collisions are ordinal/case-sensitive. A null/false slash flag is not enabled. `autoinvoke: false` does not disable explicit mention/manual selection. Generic slash text insertion/marks remain the root owner's work; these helpers do not execute a command or activate a skill.

## Standalone Session skill activation is a distinct missing boundary

The source **does** declare:

`POST /api/session/{sessionID}/skill` with `{ id?: MessageId, skill: SkillId, resume?: boolean }`, returning 204 and SessionNotFound/SkillNotFound errors.

Source `core/session.ts` resolves the Session's Location, gets the registered skill, publishes `SessionEvent.Skill.Activated` with `{ sessionID, id, name, text }`, maps a supplied `msg_` ID to an `evt_` ID, then schedules resume unless `resume:false`. The native Core activation API is not currently present. This pass does not invent that event/projector, add a fake-success activation endpoint, or emulate it by submitting an empty prompt.

The implemented UI path is **selection into PromptInput.Skills**, which already has real native admission/preparation. A future standalone activation action must be wired only after the Session owner supplies its actual event/publication API. No activation button/callback in this picker claims that capability.

## Source and verification

- `packages/protocol/src/groups/skill.ts`, `server/src/handlers/skill.ts`: list-only skill group/envelope.
- `packages/core/src/skill.ts`, existing native InstructionCatalog/SkillSources: registered identity/catalog and supported source producer behavior.
- `packages/tui/src/component/dialog-skill.tsx`: large picker, metadata formatting, loading/error/selection.
- `packages/tui/src/component/prompt/autocomplete.tsx`: @ entries, slash:true eligibility, command collision precedence.
- `packages/protocol/src/groups/session.ts` and `core/session.ts`: distinct standalone activation contract, not faked here.

Verification uses only the pinned local .NET 11 full CLI build and isolated `C:\tmp\opencode\mcp-finish-pass` artifacts. No tests, skill loading, live config/filesystem/API/DB/network/process/UI execution, clipboard, or screenshots are run. No shared root/Dialogs/project files or commits are changed.

Final full CLI dependency-graph build succeeded with **0 warnings and 0 errors**. An offline restore refreshed the other owner's updated SDK references using the isolated artifacts directory as the only restore source; no package/project reference was added by this work.
