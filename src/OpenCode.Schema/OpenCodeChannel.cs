namespace OpenCode.Schema;

/// <summary>Identity of the independently hosted .NET application channel.</summary>
public static class OpenCodeChannel
{
    public const string Name = "dotnet";
    public const string Application = "opencode-" + Name;
    public const string ServiceFileName = "service-" + Name + ".json";
    public const string DatabaseFileName = "opencode-" + Name + ".db";
    public const string ServiceVersion = "10.0.0";
    public const int ServicePort = 5055;
}
