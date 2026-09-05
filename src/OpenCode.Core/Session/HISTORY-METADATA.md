# Model history metadata

SessionHistory now uses the parent's generic LlmMessage/LlmContent metadata
contracts directly. These annotations remain separate from ProviderMetadata,
whose existing model/provider replay rules are unchanged.

Source `session/runner/to-llm-message.ts` mapping:

- User, assistant, skill, location-switched, shell and completed-compaction messages
  retain their projected IDs and source message metadata.
- User metadata is always an object. Nonempty agent attachments overwrite its
  `agents` key exactly as source's merge does; absent/empty attachments do not
  overwrite an existing metadata value. Agent references are not invented prompt text.
- Synthetic messages retain their IDs but intentionally do not inherit synthetic
  metadata into model context. System messages intentionally have neither IDs nor
  message metadata. Derived tool-role result messages do not borrow the assistant ID.
- Background shell rows remain excluded; other shell metadata now survives lowering.
- Text and directory attachments retain the existing source text labels and add
  `metadata.attachment` with source and optional name/description. No data bytes or
  mention offsets are invented in that annotation.
- Image/PDF attachments carry optional `metadata.description`. Description remains
  an annotation, not a fabricated provider title/citation/file ID or extra user text.

Media preparation already maps messages and enclosing tool results with record
`with` expressions, retaining their IDs/metadata and other attributes. Unchanged
media retain their annotations. Source modality-error/image-removal replacements
remain new text parts rather than pretending the removed media are still present.

Only now-representable guards were removed. Unsettled tool history, unsupported
attachment MIME types and remote/managed tool-file materialization remain explicit
boundaries. Provider-specific interpretation of recognized generic media metadata
is owned by the existing LLM lowering code, which this pass did not edit.

Verification is isolated pinned .NET compilation and static checks only, with no
tests or runtime model/history/database operations.
