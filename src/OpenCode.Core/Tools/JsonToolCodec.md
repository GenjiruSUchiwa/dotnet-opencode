# Tool JSON Schema pattern support

## Registration blocker

The exact `Unsupported tool schema keyword: root.sessionID.pattern` diagnostic came
from `JsonToolCodec.CheckSchema`, not CodeMode signature conversion or an inference
provider. The production path is:

1. `ToolLocationFactory.CreateAsync` constructs the available builtins, including
   `SubagentTool` when the shared subagent service is composed.
2. `SubagentTool.Create` passes both input and output schemas to `ToolInfo.FromJson`.
3. `FromJson` immediately constructs `JsonToolCodec` for each schema.
4. Its constructor walks `properties.sessionID`, encounters `pattern`, and previously
   rejected it as an unknown keyword. The exception prevents native plugin activation
   and a usable Location tool snapshot, even if the user never invokes a subagent.

Both Subagent schemas deliberately use `^ses`, matching upstream
`packages/schema/src/session-id.ts`. The source accepts legacy IDs starting with
`ses`; newly generated IDs still use `ses_`. Neither schema nor the scalar domain
was changed to work around this error.

## Fixed validation path

`pattern` must be a string and is compiled during codec construction. Invalid or
unsupported patterns fail registration explicitly. The original schema is cloned
unchanged and remains the schema carried by the tool definition and snapshot.

Input `DecodeAsync` and output `EncodeAsync` both pass through `CheckValue`, which
enforces the assertion on strings, including nested properties/items/combinators.
Like JSON Schema, `pattern` alone does not constrain a non-string value; a `type`
assertion still controls its domain. A failed match reports a `ToolValidationIssue`
at the value's path and travels through the existing `ToolSnapshot` input/output
failure handling. No executor, permission rule, success result, or registry bypass
was added.

`ToolSnapshot.Definition` retains the complete JSON schemas. Ordinary provider
lowering copies those definitions into provider parameters/input_schema. Existing
provider-specific projection (including Gemini's restricted schema vocabulary) is
unchanged; it is not the source of this registration failure. Native execution
validation retains the pattern regardless of provider projection. CodeMode's
TypeScript-like signature is descriptive, not the enforcement boundary; nested
execution still uses the captured codec. No CodeMode/provider file was edited.

## Regex semantics and limits

The source JSON Schema importer's applied pattern check is
`new RegExp(pattern).test(value)` with empty flags
(`effect/src/internal/schema/fromJsonSchemaDocument.ts`). The typed source
Subagent input uses `SessionSchema.ID`, whose prefix constraint is the same `^ses`.
The upstream raw-JSON-schema importer's best-effort fallback is **not** copied:
the native codec continues to reject unknown/unsupported assertions rather than
accept input without validation.

`JsonToolPattern` uses the **existing Core Acornima 1.7.0 dependency**, specifically
`Tokenizer.AdaptRegExp`, not a JS engine or raw `RegexOptions.ECMAScript` shortcut.
The adapter handles ECMAScript syntax, anchors, dot/line-terminator behavior,
escapes, and character classes. Matching is case-sensitive, flagless UTF-16 and
unanchored unless the pattern itself supplies anchors. Empty patterns remain valid.
Slash characters are pattern data, not `/literal/flags` delimiters.

The supported surface is deliberately narrower than all ECMAScript RegExp:

- Backreference-like escapes (`\1` through `\9`, and `\k`) are rejected, including
  inside character classes. This avoids the adapter's documented self/forward
  reference and repeated-capture discrepancies. Escaped literal backslashes are
  distinguished from these escapes.
- Inline modifiers and group syntax other than ordinary/named/noncapturing groups
  and lookarounds are rejected. In particular, case-insensitive scoped matching is
  not claimed equivalent across JS and .NET.
- Remaining constructs must pass Acornima syntax validation and be convertible by
  that adapter. No fallback accepts an unconverted pattern.
- No external flags, Unicode `u`/`v` mode, or full Unicode-property matching are
  advertised. The source applied JSON Schema check also supplies empty flags.
- Matching has a 250 ms per-assertion limit. Timeout throws a validation failure for
  the whole operation; it is **not** a non-match that `not`/`oneOf` can reinterpret
  as success. Caller cancellation is checked before traversal and after matching,
  including on timeout. There is no claim of a whole-document wall-clock budget.

Acornima's README documents the conversion limitations. Its public adapter and
conversion-error type are marked deprecated for the next major version. Exactly
that call/catch region suppresses CS0618, with the existing pinned dependency
documented in source. Upgrading/removing the adapter requires an explicit equivalent
implementation/dependency decision; it must not be replaced by unconstrained input
acceptance or raw .NET regex matching. No dependency/project configuration changed.

## Builtin audit and remaining constraints

The current `Tools/Builtins` creators and shared `BuiltinToolSchemas`, plus the
referenced `ShellToolOutput.Schema`, were inspected. Their other assertion keywords
are already supported: type, properties, required, items, additionalProperties,
enum/const, oneOf, numeric bounds, and length/item bounds. `pattern` used as a
glob/grep **input property name** is not a JSON Schema assertion; `format` used as
a webfetch property name is likewise not the `format` assertion keyword. The only
current builtin `pattern` assertions are the two Subagent Session ID constraints.

Unknown keywords still fail in the original default case. This fix does not add
`patternProperties`, `$ref`/definitions, `format`, `uniqueItems`, or `multipleOf`.
Existing numeric representation, Unicode length counting, depth, and issue-count
limits remain unchanged. It does not claim general JSON Schema completeness.

## Verification boundary

Verification is source tracing and pinned .NET 11 build only, with
`OpenApiGenerateDocuments=false` and isolated `C:/tmp/opencode/tools-pattern-f945b861`
artifacts. No test was added/edited/run. No codec, regex/parser helper, serializer,
tool registration, model/provider, CLI/SDK/DI, MCP, native, DB, or application code
was executed for verification. The selected `^ses` schema is accepted by the new
source path and enforced by both codec directions; runtime confirmation remains
pending user authorization. Build results do not constitute runtime verification.

Final Core and Server builds both succeeded with **0 warnings and 0 errors**.
Logs: `C:/tmp/opencode/tools-pattern-f945b861/core-build.log` and
`C:/tmp/opencode/tools-pattern-f945b861/server-build.log`.
Changed files are only `JsonToolCodec.cs`, new `JsonToolPattern.cs`, and this note.
