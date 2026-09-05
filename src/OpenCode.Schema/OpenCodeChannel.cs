namespace OpenCode.Schema;

/// <summary>Identity of the independently hosted .NET application channel.</summary>
public static class OpenCodeChannel
{
#if OPENCODE_DOTNET_LOCAL
    public const string Name = "dotnet-local";
#else
    public const string Name = "dotnet";
#endif
    public static bool IsLocal => Name == "dotnet-local";
    public const string Application = "opencode-" + Name;
    // Public HTTP identity is independent of the persisted application/channel names.
    public const string UserAgent = "dotnet-opencode";
    public const string ServiceFileName = "service-" + Name + ".json";
    public const string DatabaseFileName = "opencode-" + Name + ".db";
    public const string ServiceVersion = "10.0.0";
    public const int ServicePort = 5055;
}
