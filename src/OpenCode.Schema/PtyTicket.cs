namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record PtyConnectToken(
    string Ticket, double ExpiresIn
)
{
    [JsonPropertyName("ticket"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Ticket { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Ticket);
    [JsonPropertyName("expires_in"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter))]
    public double ExpiresIn { get; init => field = MessageContract.Integer(value, 1); } = MessageContract.Integer(ExpiresIn, 1);
}
