namespace OpenCode.Server.Documentation;

using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;
using OpenCode.Schema;

/// <summary>Documentation-only metadata. It never invokes a handler or constructs a domain service.</summary>
internal sealed record NativeContract(Type? Request, Type? Response, int Status = 200, string? Unavailable = null,
    string? Note = null, string ContentType = "application/json");

internal sealed record NativeHealth(bool Healthy, string Version, double Pid, string Id, string Application, string Channel,
    string State, [property: JsonPropertyName("buildID")] string BuildId);
internal sealed record GeneratedText(string Text);

internal static class RegisteredRoutes
{
    internal static IEnumerable<(RouteEndpoint Endpoint, string Method, string Path)> All(EndpointDataSource source) =>
        source.Endpoints.OfType<RouteEndpoint>().SelectMany(endpoint =>
            (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (endpoint, method.ToUpperInvariant(), Path(endpoint.RoutePattern))));

    internal static string Path(RoutePattern pattern) => "/" + string.Join("/", pattern.PathSegments.Select(segment =>
        string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => parameter.IsCatchAll ? "*" : "{" + parameter.Name + "}",
            _ => throw new InvalidOperationException("Unsupported route-pattern part.")
        }))));

    internal static Type? Request(RouteEndpoint endpoint) => endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
    internal static string? Handler(RouteEndpoint endpoint) => endpoint.Metadata.GetMetadata<MethodInfo>()?.DeclaringType?.FullName;
}
