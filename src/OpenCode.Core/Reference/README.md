# Local Reference Sources

`ReferenceSources.Observe` implements local declaration materialization from
`config/plugin/reference.ts` and `reference.ts`; `Instructions` supplies the
explicit `core/reference-guidance` source.

Each ordered document retains its path. Relative local references resolve against
that document's directory, virtual config against the Location directory, and `~/`
against global home. Later declarations replace the entire earlier definition for
the same alias, not individual fields. Invalid aliases are excluded using the
source's slash/whitespace/backtick/comma rule.

Supported forms are `.`, `/`, or `~`-prefixed string shorthands and objects with a
`path`, optional `description`, and optional `hidden`. For Windows absolute paths,
the explicit object form is the upstream-defined local discriminator. Local
reference materialization does not require the target directory to exist; it does
not claim to clone or read its contents.

Guidance includes described references, sorts names by host locale, and contains
name/path/description only. As upstream specifies, hidden does not filter this
guidance list. Initial blocks and addition/removal/replacement text match
`reference/instructions.ts` and flow through existing instruction hashing and
chronological delta persistence.

Git/repository references still fail explicitly until Repository/RepositoryCache
materialization exists. Reference listing/guidance does not authorize filesystem
tools or grant access beyond the tool policy. No network or tool execution was added.
