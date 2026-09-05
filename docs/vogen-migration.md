# Vogen 8.0.7 migration and CLI handoff

Status: all 25 scalar wrappers and their non-CLI consumers are migrated to Vogen.
There are no remaining handwritten scalar-ID exceptions. The full CLI
dependency build passed with 0 warnings/errors; no CLI file was changed by the
non-CLI owner. This document is the stable factory contract for the CLI owner.
Do not construct migrated wrappers with `new`, use
`default` as an absence sentinel, or initialize their scalar properties with
object initializers/`with`. Use nullable wrappers for absence.

All 25 wrappers retain/add `FromExisting(value)`:

- String: SessionId, ProjectId, MessageId, AgentId, ProviderId, ModelId, VariantId,
  ModelFamily, PluginId, SnapshotId, SkillId, CredentialId, IntegrationId,
  IntegrationMethodId, IntegrationAttemptId, EventId, FormId, PermissionId,
  PermissionSavedId, PtyId, ShellId, WorkspaceId.
- Double: Money, MoneyPerMillionTokens, PersistentPtyReadLines.

Existing factories remain: SessionId/ProjectId/MessageId/CredentialId/IntegrationId/
IntegrationAttemptId/EventId/FormId/PermissionSavedId/PtyId/ShellId/WorkspaceId
`Create()`, FormId `Create(string?)`, PermissionId `Create(string? = null)`, and
PtyId/ShellId `Ascending()` / `Ascending(string)`. PluginId/SnapshotId/SkillId retain
`FromExisting` and now use Vogen like the other IDs. Money and
MoneyPerMillionTokens retain `Zero` and the read-only `Amount` alias.

Existing explicit scalar casts route through `FromExisting`; conversions to the
underlying scalar remain available where previously present. SessionId ordering
remains ordinal. Identifier generation is not changed.

For migrated types, public scalar constructors and record `init` surfaces are intentionally removed:
this requires C# call-site migration and is **not binary compatibility**. Generated
default structs are uninitialized, not legitimate empty IDs or numeric zeros.
Legacy positional deconstruction and explicit ToString formats are retained.
Do not compare default wrappers as absence markers. Guard `IsInitialized()` before
reading a potentially uninitialized value at a validation boundary.

Custom scalar JSON codecs are retained as source-contract adapters over Vogen-backed
values, rather than replacing their token/error behavior with generator
defaults. `ScalarJsonConverter<T, TScalar>` provides dictionary-key conversions
through the same retained factory and underlying JSON codec. Its
`IScalarJsonConverter.ScalarType` metadata identifies the wire primitive;
`NativeSchemaExporter` keeps explicit constrained schemas and rejects unmapped
scalar codecs, rather than emitting unknown `{}`. Nullable value wrappers continue
to use existing nullable/optional codecs. No database column, stored scalar, ID
generation algorithm, timestamp ordering or random-character distribution changes.

## Validation inventory

| Types | Retained acceptance |
| --- | --- |
| SessionId | Non-whitespace string beginning `ses`, not necessarily `ses_` |
| MessageId, PermissionId, PermissionSavedId, WorkspaceId | Non-whitespace string; no new prefix rule |
| ProjectId, CredentialId, IntegrationId | Any non-null string, including empty/whitespace |
| AgentId, ProviderId, ModelId, VariantId, ModelFamily, IntegrationMethodId, IntegrationAttemptId | Any non-null string, with the existing prompt-JSON null error |
| PluginId, SnapshotId, SkillId | Any non-null string, including empty/whitespace; Prefix constants impose no validation |
| EventId, FormId, ShellId | String beginning `evt_`, `frm_`, `sh_`, respectively |
| PtyId | String beginning `pty`, not necessarily `pty_` |
| Money, MoneyPerMillionTokens | Finite double; negative values are not newly rejected |
| PersistentPtyReadLines | Finite integer double from 1 through 65535 |

`FromExisting`, preserved explicit casts and `Create` call the contract validation
before Vogen's factory. Generated `From`/`TryFrom` also enforce the accepted-value
domain, but are new APIs, not promises of legacy exception text. Existing JSON and
request validation use `IsInitialized()` to retain their original JsonException or
ArgumentException path rather than accidentally reading an uninitialized Value.
SessionId's manually retained CompareTo uses `string.CompareOrdinal`; comparison
generation is omitted for all wrappers. Vogen provides initialized typed equality
and hashing; primitive casts are the existing explicit implementations.

## Correction of native null acceptance

The initial exclusion of PluginId, SnapshotId and SkillId was incorrect. Upstream
`packages/schema/src/plugin.ts:7`, `snapshot.ts:5`, and `skill.ts:8` define each ID
as `Schema.String.pipe(Schema.brand(...))`. Null acceptance in the previous C#
positional wrappers was a native-port bug, not a valid source compatibility domain.
All three now reject null through Vogen-backed `FromExisting` and canonical string
JSON codecs, while accepting every non-null string without normalization or a
prefix restriction. This fixes native invalid-input acceptance; it does not remove
valid source inputs. The documented C# constructor/binary changes still apply.

No sentinel or initialization bypass is used. Genuine absence remains an outer
nullable wrapper: PluginInfo.Id, snapshot capture results and snapshot fields.
PluginInfo.Id and SessionRevert.Snapshot omit absent values and reject explicit
JSON null through the existing optional-value codec. Skill validation checks
IsInitialized before accessing Value; it does not depend on default.Value being
null. The native exporter maps these three codecs to an unrestricted, non-null
string schema, without prefix or minimum-length constraints.

## Non-candidates

- **Discriminated-union cases:** FormValue/FormConditionValue, ConfigDuration
  variants, config shorthand/toggle variants, tool-content and prompt-source cases
  retain their inheritance/discriminator semantics. A scalar-looking constructor
  does not make these independent scalar value objects.
- **Object-shaped DTOs and event envelopes:** ModelTime, ModelCostTier (which also
  has a discriminator), ShellTimeoutInput, single-field event payloads and other
  request/config records keep their named-property JSON contracts and reference
  optional/null semantics. They are not scalar brands.
- Collection wrappers such as FormAnswer, composite records, enums, native ABI
  structs and handles are not candidates. OpenTui projects remain unchanged.

## Consumer and CLI handoff

Schema, Core, Protocol, Server, Client and SDK directly pin Vogen 8.0.7 so its
analyzers run in each owned project; no VOG diagnostics or validation switches are
disabled. CLI should also reference that pinned package when enabling its analyzer
audit. Qualify `OpenCode.Schema.SessionId.FromExisting` where a property shadows
the type name.

The last-three follow-up keeps all FromExisting signatures stable and does not
require transport API changes. The CLI owner has replaced the legacy
`skill.Id.Value is null` check in `Tui/Skills/SkillCatalogSnapshot.cs` with
`!skill.Id.IsInitialized()`. CLI files remain outside this worker's scope.
The Pipelines owner retains all transport buffer/framing files; this follow-up
does not edit those implementations or shared package ItemGroups.

`ToolContext.MessageId` is now nullable because command interpolation genuinely has
no model-message source. Model tools use `RequireMessageId()` before emitting tool
permission/question provenance. This replaces the previous default-ID sentinel;
it does not generate a fake message or alter the command permission source.
Event JSON parsing similarly uses `EventId?` while waiting for the required field.
PTY lookup absence uses TryGetValue rather than a default wrapper.

## Verification

Built with local SDK `11.0.100-preview.7.26381.103`,
`--artifacts-path C:\tmp\opencode\vogen-build` and
`-p:OpenApiGenerateDocuments=false`. The full `OpenCode.Cli.csproj` dependency build
includes Schema, Protocol, Core, Client, Server, SDK, and the unchanged OpenTui
dependencies. Source searches found no remaining explicit scalar constructor calls
in non-CLI consumers; the compiler covered target-typed constructors, and enabled
Vogen analyzers covered explicit default construction. A final declaration audit
found 25 ValueObject attributes and no remaining handwritten record-struct scalar
wrappers in Schema. The full build remains compilation-only verification.

Generator API/source was read from the pinned `8.0.7` tag, including
`ValueObjectAttribute`, `VogenDefaultsAttribute`, comparison/cast/conversion flags,
validation emission, equality/hash generation and predefined-instance validation.
Generated C# was inspected from isolated build output, not edited or executed.

No CLI files are owned by this migration worker. No tests or runtime probes are
authorized; compilation alone does not establish serialization/runtime parity.
