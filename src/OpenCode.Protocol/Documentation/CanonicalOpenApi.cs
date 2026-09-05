namespace OpenCode.Protocol.Documentation;

using System.Security.Cryptography;
using System.Text.Json.Nodes;

/// <summary>Pinned TypeScript protocol metadata, never an application/handler discovery executable.</summary>
public static class CanonicalOpenApi
{
    public static (JsonObject Document, string Sha256) Read()
    {
        using var source = typeof(CanonicalOpenApi).Assembly.GetManifestResourceStream("OpenCode.Protocol.OpenApi.Source.json")
            ?? throw new InvalidOperationException("The canonical OpenAPI reference is not embedded.");
        using var checksum = typeof(CanonicalOpenApi).Assembly.GetManifestResourceStream("OpenCode.Protocol.OpenApi.Source.sha256")
            ?? throw new InvalidOperationException("The canonical OpenAPI reference hash is not embedded.");
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        using var reader = new StreamReader(checksum);
        var expected = reader.ReadToEnd().Trim();
        var digest = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        if (!digest.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The canonical OpenAPI reference hash does not match.");
        var document = JsonNode.Parse(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))) as JsonObject
            ?? throw new InvalidDataException("The canonical OpenAPI reference must be an object.");
        if (document["openapi"]?.GetValue<string>() != "3.1.0" || document["paths"] is not JsonObject || document["components"]?["schemas"] is not JsonObject)
            throw new InvalidDataException("The canonical OpenAPI reference has an unsupported shape.");
        return (document, digest.ToLowerInvariant());
    }
}
