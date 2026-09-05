# Mounted slash commands

The production root mounts `CommandAutocomplete` above the actual measured
`command-composer` node, using the host's public layout tree. No guessed Home
height, duplicate render tree, or second event stream is used. The popup has the
source left/right split border, at most ten rows, title/description matching,
prefix priority, wrapping arrow/Ctrl+P/N selection, Enter/Tab completion, Escape
dismissal, and pointer selection. Selecting a server command inserts `/name `;
it does not execute prematurely. Registered client slash commands execute their
real existing callbacks. Unregistered client features are not inserted into the list.

`InteractiveTui` fetches the actual `SessionHttpClient.ListCommandsAsync` catalog
at the selected location. The source name and description are retained. Client
aliases come from `packages/tui/src/app.tsx` only for implemented registrations.
Unknown slash names stay in the editor with an error, never fall through to a
plain model prompt. Shell `!` is likewise explicit and blocked until its real
shell route is connected. Skills/file/agent reference completion is separate work.

## Admission and observation

`SessionClientAdapter.Commands.cs` uses the existing observer:

1. Load the requested Session, or create a real Session with the captured location,
   agent, and model. No command inbox/message identity is invented.
2. Call `ObserveSessionAsync` before admission.
3. Invoke the session-ready callback so a newly created Session is reachable in
   its origin tab **before** command interpolation can request permission.
4. Acquire the observer entry's existing `Admission` semaphore.
5. Call the canonical `ExecuteCommandAsync` with the exact command name, argument
   text, URI file attachments, agent attachments, skill IDs, and delivery mode.
6. Call `RefreshObservationAsync` on that same Session. The shared global event
   receiver supplies subsequent messages; there is no second command stream loop.

The command HTTP operation may wait for human permission during shell interpolation,
so it uses the linked app/adapter cancellation lifetime rather than the short
lookup timeout. Existing-session agent/model selection is not rewritten by the
command path; source command dispatch also uses the already selected Session.

The root captures and clears its editor synchronously. A failed admission restores
only an empty origin draft, including retained attachments. Later text, another
tab's editor, and closed views are not replaced. Background admission errors are
retained for their origin rather than written into an unrelated active composer.

## Shared prompt-document handoff

Root helpers `CapturePromptInput`, `ClearPromptAttachments`, and
`RestorePromptAttachments` preserve complete canonical input after Message Actions
revert. URI-backed files keep their URI; inline files become MIME/base64 data URIs;
names, descriptions, mentions, agents, and skill IDs are retained. Normal prompt
admission now captures the complete protocol input before clearing and sends it
through `NetworkPromptInput`. `CanSubmitPrompt` gates that capture using the actual
feed/admission state. Queue sets its delivery mode without inventing a retry ID.

## Related mounted controls

The root also mounts `DialogMessage`, code copying, and source/rendered Markdown.
Message copy uses canonical text, not rendered tool/reasoning labels. Revert uses
the typed HTTP staging endpoint and shared observer refresh. Fork uses the typed
Client before-message boundary and hydrates before creating its tab. The host handles transcript-selection
Ctrl+C before root dispatch; controlled prompt selection has its own single-flight
copy path. No second transcript-selection copy handler was added.
The generic host's styled/cross-block selection exposes native display ranges;
the root does not slice strings using those ranges. Its separate controlled
prompt editor continues to use the input API's UTF-16 cursor/selection indexes.

`ApplyRenderColors` supplies semantic foreground/background, cursor, and formfield
selection colors to `OpenTuiHost.Colors`, including the actual frame clear.
Integration/MCP management screens use the same authenticated client and an
identity-keyed location; their own mutations and the existing shared event-feed
hook refresh catalogs. No duplicate SSE subscription is created. Persistent terminal
controls now mount the native pane only after capability and actual PTY identity
checks; missing deployment support is shown as unavailable.

Sources: `component/prompt/autocomplete.tsx`, `component/prompt/index.tsx:1133–1302`,
`prompt/parse.ts`, `prompt/display.ts`, `prompt/codec.ts`, and
`routes/session/dialog-message.tsx` under the original `packages/tui/src`.
Verification is isolated .NET 11 CLI builds only; no runtime, clipboard, API,
database, browser, screenshots, or tests were executed.
