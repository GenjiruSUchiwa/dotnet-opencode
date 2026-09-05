# Generic API command

The complete CLI now uses one typed System.CommandLine tree. ApiCommand receives
ApiOptions directly; ApiOptions.Parse and manual help/argv scanning were removed.
See CommandLine/README.md for lexical compatibility and package/API evidence.

Source: packages/cli/src/commands/handlers/api.ts and the api specification in
commands/commands.ts. Parameter grammar was checked against the installed Effect
4.0.0-rc.112 CLI Primitive.keyValuePair implementation, not inferred from curl.

## Supported surface

```
OpenCode.Cli api GET /api/health
OpenCode.Cli api v2.session.get --param sessionID=ses_example
OpenCode.Cli api POST /api/session --data '{"title":"Example"}'
```

These are examples, not executed verification commands. Recognized source flags:
--data/-d, --header/-H (up to 100), --param, --server, --standalone and help.
Methods are DELETE, GET, HEAD, OPTIONS, PATCH, POST and PUT, case-insensitive for
the raw method form. Exactly one operation ID or two raw-request arguments are
required. No TRACE/CONNECT, --fail, --output, --format, body-file expansion or stdin
body convention is invented. `--data @file` and `--data -` are literal strings.

Help does not connect. --server and --standalone conflict before acquiring a host.
Program.cs adds only api dispatch and the usage text; Run/Stats/TUI dispatch and
their lifetime/permission rules are unchanged.

## Operation lookup and encoding

Raw method/path requests do not fetch OpenAPI and do not add --param values to
their path; put a raw query in the supplied path, as source does.

Operation-ID requests load `/openapi.json` from the selected authenticated origin.
User --data/--header overrides are not used for schema discovery. A non-2xx schema
response fails with `Failed to load OpenAPI document: HTTP N`. Invalid JSON/schema
structure and missing operation/required path parameters are real command errors.
There is no bundled-spec fallback or invented empty operation map.

The resolver visits paths/methods in document order, recognizes only source lower-
case HTTP-method keys, and selects the first exact operationId match. It does not
use document/path/operation `servers`, `$ref` expansion, parameter serialization
style metadata, or invented operation aliases. This matches the simple source
resolver, rather than pretending to be a complete OpenAPI execution engine.

Path placeholders use encodeURIComponent rules (including the source treatment of
`!'()*~` and strict malformed-surrogate failure). Unused parameters use the source
URLSearchParams form encoding: '+' for spaces, uppercase percent escapes, and '~'
escaped. Numeric object-index parameter names sort before other insertion-ordered
names as with Object.entries. Repeated parameter keys keep the last value without
moving their insertion position. The installed flag primitive requires exactly
one '=' and nonempty key/value; values are strings, not parsed JSON. Repeated
--param flags are supported; one key=value token belongs to each flag, matching
the installed source lexer. The earlier ad-hoc consecutive-token extension was
removed. The shared tree handles source short flags, boolean coercion and negation.

## Headers, payload and origin confinement

Headers split at the first colon and trim names/values. Names are case-insensitive;
later values replace earlier ones, including an explicit Authorization override.
The selected endpoint's Basic credential is otherwise applied. Header names and
values are validated before sending; private generated values are not printed.
Content-Type defaults to application/json only when --data is present and no
user content-type was supplied. The body is literal UTF-8, not prevalidated JSON.
GET/HEAD with --data fails, matching Fetch's body restriction.

The new `OpenCode.Client.ApiHttpClient` is a network-only raw transport. It does
not depend on Core/Server, run a model, or use a typed-response success shortcut.
It owns a non-auto-redirect HttpClient so callers cannot accidentally inject an
auto-following handler. Request targets, redirect targets and Host overrides must
retain the selected scheme/host/port with no URI userinfo. This rejects malicious
OpenAPI path targets and network-path references before attaching credentials.
No credential is forwarded to a document's servers array or foreign redirect.

Same-origin 301/302/303/307/308 redirects are handled explicitly, with Fetch-style
POST/303 method/body conversion and a 20-hop ceiling. Cross-origin redirects are
rejected, not followed anonymously and not followed with the private password.
This origin restriction is the requested safety boundary beyond the source's
unrestricted Fetch URL handling. Platform URI normalization and native HTTP
connection/header defaults may differ from WHATWG/Bun in unusual URL cases.

## Output, HTTP errors and streaming

The upstream handler always calls response.text() and does not check final
response.ok. Therefore this command also prints real HTTP error bodies and returns
0 for a completely received final HTTP response, including 4xx/5xx. It does not
substitute a typed success object or an empty result. OpenAPI-load failures,
transport/parse/origin errors return 1; caller cancellation returns 130.

A 204/HEAD/empty body prints nothing. Every nonempty body is UTF-8 decoded with
replacement for malformed bytes and an initial UTF-8 BOM removed, regardless of
Content-Type/charset. Thus binary data follows source **text** behavior; this is
not a byte-exact binary download command and no base64 wrapper is invented.
HTTP decompression is enabled. An OS newline is appended only when nonempty output
does not already end in that exact newline sequence, matching source EOL handling.

The response is decoded/written/flushed incrementally, including SSE text. There
is no SSE parser, replay/reconnect loop, fabricated event envelope or completion
marker. The UTF-8 decoder and output chunks preserve split characters. This differs
from source timing, which buffers response.text() until EOF: finite output is
equivalent, while an open stream becomes observable immediately. A later stream
failure can leave genuine partial output and a nonzero exit. No fixed request
deadline is imposed; caller cancellation closes the owned response/client.

The OpenAPI document is fully materialized for lookup; ordinary response output
uses bounded character buffers. No guessed size cap or silent truncation is added.
Native JSON parser/calendar/URI behavior is not presented as runtime-proven parity.

## Connection and ownership

Managed selection uses the existing dotnet ServiceDaemon policy (stricter build
identity than source api's managed mismatch-ignore mode). Explicit selection uses
authenticated read-only service inspection, warns on version mismatch, and never
starts/replaces that endpoint. Its password comes from the native CLI variable
OPENCODE_DOTNET_SERVER_PASSWORD, not another channel's registration.

Standalone uses StandaloneHostLease and the real private HTTP API. Response and
raw client disposal occur before lease disposal on success, error or cancellation.
No elected service is stopped and no new DB recovery rights are asserted. The
command never calls an official launcher, Node/Bun, embedded SDK text generation,
or spec-provided external origin.

## Evidence

The final pinned .NET 11 isolated full-CLI build passed with zero warnings and
zero errors, with OpenApiGenerateDocuments=false. The first attempt stopped at
other-owned Core/WebSearch/WebSearchRuntime.cs:99 (missing updated); that blocker
cleared before final validation without edits here. No command, HTTP/spec request,
listener, DI startup, database, test, process/native/model execution or production
access was used for verification. No commits or delegation occurred.

## License

Adapted from OpenCode, MIT License, copyright (c) 2025 opencode. Permission is hereby
granted, free of charge, to any person obtaining a copy of this software and its
documentation (the Software), to deal in the Software without restriction,
including rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies, and permit others to do so, subject to inclusion of this
copyright and permission notice in copies or substantial portions. THE SOFTWARE
IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR
OTHER LIABILITY ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR ITS USE.
