namespace OpenCode.Server.Documentation;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCode.Protocol;
using OpenCode.Protocol.Documentation;
using OpenCode.Schema;

/// <summary>Builds documentation from registered route metadata only. It never invokes application handlers.</summary>
public static class OpenApiDocument
{
    public static void MapNativeOpenApi(this IEndpointRouteBuilder app) => app.MapGet("/openapi.json", (EndpointDataSource routes) =>
        Results.Json(Create(routes))).WithName("native.openapi.document");

    public static JsonObject Create(EndpointDataSource routes)
    {
        var reference = CanonicalOpenApi.Read();
        var document = reference.Document;
        var canonicalPaths = document["paths"]!.AsObject();
        var schemas = document["components"]!["schemas"]!.AsObject();
        var exporter = new NativeSchemaExporter(schemas);
        var contracts = ContractCatalog.Create();
        var paths = new JsonObject();
        var unmapped = new JsonArray();
        var unavailable = new JsonArray();
        var absent = new JsonArray();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var canonical = canonicalPaths.SelectMany(path => path.Value!.AsObject()
            .Where(operation => Methods.Contains(operation.Key)).Select(operation =>
                (Path: path.Key, Method: operation.Key.ToUpperInvariant(), Operation: operation.Value!.AsObject())))
            .GroupBy(item => item.Method + " " + Shape(item.Path)).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var registered = RegisteredRoutes.All(routes).Where(route => route.Path != "/openapi.json").ToArray();
        foreach (var group in registered.GroupBy(route => route.Method + " " + Shape(route.Path), StringComparer.Ordinal))
        {
            var actual = group.First();
            if (group.Count() != 1)
            {
                unmapped.Add(Issue(actual.Method, actual.Path, "Duplicate registered method/path; no contract was guessed."));
                continue;
            }
            if (!canonical.TryGetValue(group.Key, out var matches) || matches.Length != 1)
            {
                unmapped.Add(Issue(actual.Method, actual.Path, "No unique canonical source operation. Native extension metadata is not yet declared."));
                continue;
            }
            var source = matches[0];
            var id = source.Operation["operationId"]!.GetValue<string>();
            found.Add(id);
            if (!contracts.TryGetValue(id, out var contract))
            {
                unmapped.Add(Issue(actual.Method, actual.Path, "Registered handler has no verified native request/response metadata.", id));
                continue;
            }
            var bound = RegisteredRoutes.Request(actual.Endpoint);
            if (bound is not null && contract.Request is not null && bound != contract.Request)
            {
                unmapped.Add(Issue(actual.Method, actual.Path, "Framework request type differs from verified contract metadata.", id));
                continue;
            }
            try
            {
                var operation = source.Operation.DeepClone().AsObject();
                operation["x-native-route"] = actual.Path;
                operation["x-native-handler"] = RegisteredRoutes.Handler(actual.Endpoint);
                operation["x-native-contract-status"] = contract.Unavailable is null ? "mapped" : "unavailable";
                if (contract.Note is not null) operation["x-native-limitations"] = contract.Note;
                operation["security"] = Security(contract.ContentType == "websocket");
                if (contract.Unavailable is not null)
                {
                    operation.Remove("requestBody");
                    operation["responses"] = new JsonObject
                    {
                        ["503"] = Error("ServiceUnavailableErrorEncoded", contract.Unavailable),
                        ["401"] = Error("UnauthorizedErrorEncoded", "Authentication required")
                    };
                    unavailable.Add(Issue(actual.Method, actual.Path, contract.Unavailable, id));
                }
                else
                {
                    if (contract.Request is not null)
                    {
                        // Keep the canonical accepted contract and expose the actual native
                        // codec schema alongside it. Differences are visible, not silently
                        // replaced with an untyped object or a claimed identical decoder.
                        var nativeRequest = exporter.Reference(contract.Request);
                        operation["x-native-request-schema"] = nativeRequest;
                        operation["x-native-request-type"] = contract.Request.FullName;
                        if (operation["requestBody"] is null)
                            operation["requestBody"] = new JsonObject { ["required"] = true, ["content"] = Content("application/json", nativeRequest.DeepClone()) };
                    }
                    var responses = operation["responses"]!.AsObject();
                    foreach (var status in responses.Select(pair => pair.Key).Where(status => status.StartsWith('2')).ToArray()) responses.Remove(status);
                    JsonObject success;
                    if (contract.Status == 204) success = new JsonObject { ["description"] = "No content" };
                    else if (contract.ContentType == "websocket")
                    {
                        operation["x-websocket"] = true;
                        success = new JsonObject { ["description"] = "WebSocket upgrade; replay/control frames follow the declared PTY protocol." };
                    }
                    else if (contract.ContentType == "application/octet-stream")
                        success = new JsonObject { ["description"] = "Raw file bytes, with the actual file MIME type", ["content"] = Content(contract.ContentType,
                            new JsonObject { ["type"] = "string", ["format"] = "binary" }) };
                    else
                    {
                        if (contract.Response is null) throw new InvalidOperationException("A JSON/stream success contract needs a response type.");
                        var response = exporter.Reference(contract.Response);
                        operation["x-native-response-type"] = contract.Response.FullName;
                        success = contract.ContentType == "text/event-stream"
                            ? new JsonObject { ["description"] = "SSE data frames plus heartbeat comments", ["content"] = Content(contract.ContentType,
                                new JsonObject { ["type"] = "string", ["x-event-envelope"] = response }) }
                            : new JsonObject { ["description"] = contract.Response.Name, ["content"] = Content(contract.ContentType, response) };
                    }
                    responses[contract.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = success;
                    responses["401"] ??= Error("UnauthorizedErrorEncoded", "Authentication required");
                    if (id == "v2.health.get")
                    {
                        responses["500"] = success.DeepClone();
                        responses["503"] = success.DeepClone();
                    }
                    else responses["503"] ??= Error("ServiceUnavailableErrorEncoded", "Native capability or Location unavailable");
                }
                var referenceErrors = SchemaReferences.Check(schemas, operation);
                if (referenceErrors.Count > 0) throw new InvalidOperationException(string.Join("; ", referenceErrors));
                paths[source.Path] ??= new JsonObject();
                paths[source.Path]![source.Method.ToLowerInvariant()] = operation;
            }
            catch (Exception error) when (error is UnmappedSchemaException or NotSupportedException or InvalidOperationException or JsonException)
            {
                unmapped.Add(Issue(actual.Method, actual.Path, "Schema metadata could not be mapped: " + error.Message, id));
            }
        }
        foreach (var operation in canonical.Values.SelectMany(value => value))
        {
            var id = operation.Operation["operationId"]!.GetValue<string>();
            if (!found.Contains(id)) absent.Add(Issue(operation.Method, operation.Path, "No registered native endpoint.", id));
        }
        document["paths"] = paths;
        document["info"] = new JsonObject
        {
            ["title"] = "OpenCode .NET registered API", ["version"] = ApplicationBuild.Version,
            ["description"] = "Canonical source operations filtered against registered native handlers and verified CLR contracts. Coverage diagnostics are not an implementation-parity claim."
        };
        document["components"]!["securitySchemes"] = new JsonObject
        {
            ["basicAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "basic", ["description"] = "Username opencode and the configured local service password." },
            ["authToken"] = new JsonObject { ["type"] = "apiKey", ["in"] = "query", ["name"] = "auth_token",
                ["description"] = "Base64 username:password. A nonempty query token takes precedence over the Authorization header." },
            ["ptyTicket"] = new JsonObject { ["type"] = "apiKey", ["in"] = "query", ["name"] = "ticket",
                ["description"] = "One-use PTY connect ticket. The connect handler validates identity/scope and allowed origin before consumption." }
        };
        document["security"] = Security(false);
        document["x-opencode-documentation"] = new JsonObject
        {
            ["source"] = "packages/protocol/openapi.json", ["sourceSha256"] = reference.Sha256, ["buildID"] = ApplicationBuild.Id,
            ["registeredOperations"] = registered.Length,
            ["documentedOperations"] = paths.Sum(path => path.Value!.AsObject().Count),
            ["unmappedContracts"] = unmapped, ["sourceOperationsNotRegistered"] = absent, ["knownUnavailableHandlers"] = unavailable,
            ["note"] = "Only matched registered operations appear in paths. Missing metadata and unavailable implementations are explicit; route counts do not imply feature parity. Native codec schemas supplement canonical request schemas.",
            ["serviceReadiness"] = "Before ready, non-health routes return authenticated 503 service_starting/service_stopping/service_failed responses; no application handlers run."
        };
        Prune(schemas, paths);
        return document;
    }

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal) { "get", "post", "put", "patch", "delete", "head", "options" };
    private static string Shape(string path) => Regex.Replace(path, @"\{[^}]+\}|\*", "{}", RegexOptions.NonBacktracking).TrimEnd('/');
    private static JsonObject Issue(string method, string path, string reason, string? id = null) => new()
        { ["method"] = method, ["path"] = path, ["operationId"] = id, ["reason"] = reason };
    private static JsonObject Content(string media, JsonNode schema) => new() { [media] = new JsonObject { ["schema"] = schema } };
    private static JsonObject Error(string component, string description) => new()
        { ["description"] = description, ["content"] = Content("application/json", new JsonObject { ["$ref"] = "#/components/schemas/" + component }) };
    private static JsonArray Security(bool ticket)
    {
        var result = new JsonArray(new JsonObject { ["basicAuth"] = new JsonArray() }, new JsonObject { ["authToken"] = new JsonArray() });
        if (ticket) result.Add(new JsonObject { ["ptyTicket"] = new JsonArray() });
        return result;
    }

    private static void Prune(JsonObject schemas, JsonObject paths)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var text) && text.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                {
                    var name = text["#/components/schemas/".Length..].Split('/')[0];
                    if (!schemas.TryGetPropertyValue(name, out var schema)) throw new InvalidOperationException("OpenAPI schema reference is unresolved: " + name);
                    if (used.Add(name)) Visit(schema);
                }
                foreach (var child in obj) Visit(child.Value);
            }
            if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(paths);
        foreach (var name in schemas.Select(pair => pair.Key).Where(name => !used.Contains(name)).ToArray()) schemas.Remove(name);
    }
}
