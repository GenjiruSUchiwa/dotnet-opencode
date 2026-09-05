# MCP HTTP reads

`SessionHttpClient.ListMcpServersAsync` reads `GET /api/mcp`.
`SessionHttpClient.McpResourceCatalogAsync` reads `GET /api/mcp/resource`.
Both accept the canonical `location[directory]` and `location[workspace]` query
fields and return the existing `OpenCode.Protocol.Groups.LocationResponse<T>`.

Schema owns server statuses, resources, templates, and JSON validation. The client
uses generated serialization metadata, retains HTTP errors through
`SessionApiException`, and does not turn missing data or unavailability into an
empty catalog. No Core/Server runtime reference or implicit service startup was
added.

Runtime mutations use `AddMcpServerAsync`, `RemoveMcpServerAsync`,
`ConnectMcpServerAsync`, and `DisconnectMcpServerAsync`. Add sends the canonical
`{config: ...}` wrapper; all require HTTP 204 and retain error bodies without
automatic retries. These are runtime operations, never configuration file edits.

Source: `packages/protocol/src/groups/mcp.ts` and
`packages/server/src/handlers/mcp.ts`.
