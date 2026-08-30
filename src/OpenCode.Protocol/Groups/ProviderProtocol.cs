namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of ProviderGroup from packages/protocol/src/groups/provider.ts
/// </summary>
public static class ProviderEndpoints
{
    public const string List = "/api/provider";
    public const string Get = "/api/provider/{providerID}";
}
