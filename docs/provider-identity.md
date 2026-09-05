# Provider HTTP identity

- Built-in model requests identify this implementation as **`dotnet-opencode`**,
  not the upstream `opencode` application.
- The shared request builder covers Google, Anthropic, OpenAI Chat Completions,
  OpenAI Responses and compatible provider endpoints, including auxiliary title
  and generation calls. OpenAI OAuth, Console authorization/catalog requests and
  native web-search providers apply the same product marker.
- The owned SDK default is `dotnet-opencode/<source build ID>`. The build ID is
  the existing application fingerprint, not an invented release version.
- Explicit provider, request and embedding-host User-Agent metadata still follows
  its existing overlay order. The final HTTP boundary prepends `dotnet-opencode`
  if the selected value does not already start with that product token. Custom
  metadata therefore supplements the rewrite identity instead of hiding it.
- This is an intentional compatibility difference requested for clear fork
  attribution. Authentication credentials, OAuth client IDs, `originator` routing,
  protocol header names and request bodies are unchanged.
- `opencode-dotnet.db`, `service-dotnet.json` and the internal application identity
  are unchanged; public User-Agent branding does not migrate persisted data.

Verification is source inspection and a pinned-SDK build only. No provider,
OAuth, SDK, application or database request was executed to verify these headers.
The full CLI dependency build completed with 0 errors and 1,979 analyzer warnings;
the existing EF/analyzer completion work remains separate. The integration log is
`provider-identity-build.log` under the external directory recorded in
`C:/tmp/opencode/modernization-integration-location.txt`.
