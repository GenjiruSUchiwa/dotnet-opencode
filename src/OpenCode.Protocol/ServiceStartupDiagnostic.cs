namespace OpenCode.Protocol;

using System.Text.Json.Serialization;

/// <summary>Private local startup report. Contains no raw exception text, configuration, commands, or credentials.</summary>
public sealed record ServiceStartupDiagnostic(
    string Nonce, string Application, string State, string Stage, int Pid,
    string? Code = null, string? Summary = null, string? Action = null,
    string[]? ExceptionTypes = null, int[]? HResults = null, string[]? Frames = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ServiceStartupDiagnostic))]
public partial class ServiceStartupJsonContext : JsonSerializerContext;
