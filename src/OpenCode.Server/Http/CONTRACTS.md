# HTTP binding and documentation boundaries

`RequestValidation` enables framework bad-request exceptions so binding failures
that occur before endpoint filters receive an actual error envelope. HTTP 400
becomes `InvalidRequestError`; an inner JsonException is classified as `Payload`.
The source's 1,024-character reason limit/suffix is preserved. The native diagnostic
text comes from System.Text.Json rather than pretending to be Effect's formatter.

Framework errors do not reliably identify Params/Query/Headers programmatically,
so those cases omit the optional kind rather than guessing from exception wording.
Non-400 transport statuses, including unsupported media and body-size errors,
retain their status. Domain endpoint error handling is not replaced.

Automatic JSON binding respects non-nullable members and required constructor
parameters and accepts out-of-order discriminators. No global ignore-null setting
is imposed, because canonical nullable response fields such as VCS base must remain
present. Explicit per-contract JSON metadata/converters retain ownership.

Source: `packages/server/src/middleware/schema-error.ts` and Effect's
`HttpApiBuilder` schema-error kinds. No malformed HTTP request or application was
executed for verification; compilation only.

OpenAPI build-time host execution is explicitly disabled with
`OpenApiGenerateDocuments=false`. No ApiDescription.Server/GetDocument target,
application launch, reflection over a running server, or generated document claiming
unregistered operations is used. The authored registered-route document and pure
Schema/Protocol metadata exporter are documented in `../Documentation/README.md`.
