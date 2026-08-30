namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record PtyConnectToken(
    [property: JsonPropertyName("ticket")] string Ticket,
    [property: JsonPropertyName("expires_in")] int ExpiresIn
);
