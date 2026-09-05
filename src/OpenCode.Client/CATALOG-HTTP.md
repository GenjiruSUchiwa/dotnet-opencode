# Catalog HTTP Client

The picker APIs are methods on `SessionHttpClient`. They use its selected
ServiceEndpoint, authentication, cancellation, JSON/status error handling, and
injected-HttpClient ownership rules. They do not load local configuration,
consult Core, contact providers directly, or cache a guessed catalog.

## Picker API

| Method | Route | Return type |
| --- | --- | --- |
| `ListAgentsAsync(directory?, workspace?, ct)` | GET `/api/agent` | `LocationResponse<IReadOnlyList<AgentInfo>>` |
| `GetAgentAsync(agentId, directory?, workspace?, ct)` | GET `/api/agent/:agentID` | `LocationResponse<AgentInfo>` |
| `ListModelsAsync(directory?, workspace?, ct)` | GET `/api/model` | `LocationResponse<IReadOnlyList<ModelInfo>>` |
| `DefaultModelAsync(directory?, workspace?, ct)` | GET `/api/model/default` | `DefaultModelResponse` |
| `ListProvidersAsync(directory?, workspace?, ct)` | GET `/api/provider` | `LocationResponse<IReadOnlyList<ProviderInfo>>` |

DTOs are in `OpenCode.Schema` and `OpenCode.Protocol.Groups`; explicit generated
metadata is `OpenCode.Protocol.CatalogProtocolJsonContext`. Lists retain the
server's order and full canonical records, including hidden/mode, enabled/status,
activation, capabilities, limits, costs, variants, and request settings. The
client does not replace missing metadata with helper defaults or silently filter
records. UI visibility/selection policy belongs to the picker owner.

Use the selected session's Location directory and workspace value when listing
its choices. Query keys are exactly `location[directory]` and
`location[workspace]`, using deep-object encoding. Omitted arguments retain the
server's default Location; no local working-directory substitution occurs.
The returned `Location` identifies the resolved placement. `workspace` is the
query key, distinct from the response Location.Ref/Info `workspaceID` field.

After the user selects an actual returned choice, use the existing mutation API:

```csharp
await client.SwitchAgentAsync(sessionId, selectedAgent.Id, cancellationToken);
await client.SwitchModelAsync(sessionId,
    new ModelRef(selectedModel.ProviderId.Value, selectedModel.Id.Value, selectedVariant?.Id.Value),
    cancellationToken);
```

Model selection uses the catalog `id` and `providerID`, not the separate API
`modelID`. Preserve the actual chosen variant. Merely listing choices does not
change session selection. Do not dump settings/header/body maps into UI logs;
the canonical catalog may contain sensitive request configuration.

## Default And Error Semantics

All five operations require HTTP 200 and JSON. Agent lookup checks that the
returned agent ID matches the request. Null arrays, null entries, missing
required records, and incomplete canonical model metadata fail explicitly.

`model.default` is `Location.response(UndefinedOr(Model.Info))`: when the server
has no default, JSON can omit `data`, and `DefaultModelResponse.Data` is null in
C#. Explicit JSON `data: null` is not this wire contract and is rejected. No
model is fabricated. The inspected native Server currently emits explicit null
in its no-default branch; that branch needs to omit `data` or serialize the
canonical DefaultModelResponse. The Client does not edit or hide that mismatch.

`AgentNotFoundError` (404) and `ServiceUnavailableError` (503) are decoded in the
existing typed error union on `SessionApiException.QueryError`; raw status/body
and JSON remain available. Model list/default and provider list declare
ServiceUnavailableError. Shared middleware can return schema/auth errors
(400/401). Unsupported or failing backend catalog operations never become empty
successes. No automatic retries are added.

The TypeScript model-list contract describes a snapshot that can precede initial
plugin settlement. A returned catalog is not proof that all execution, tool, or
plugin capabilities are ready; retain the separate execution-readiness check.

## Sources

- `packages/protocol/src/groups/agent.ts`: list/get, Location envelopes, AgentNotFoundError.
- `packages/protocol/src/groups/model.ts`: model snapshot/default and UndefinedOr data.
- `packages/protocol/src/groups/provider.ts`: provider list and unavailable error.
- `packages/protocol/src/groups/location.ts`: optional deep-object query keys.
- `packages/schema/src/{agent,model,provider,location}.ts`: canonical records.

This addition does not implement a picker dialog or alter Server behavior.
