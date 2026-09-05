namespace OpenCode.Sdk;

using OpenCode.Core.CodeMode;
using OpenCode.Core.Permissions;
using OpenCode.Core.Session;
using OpenCode.Core.Tools;
using OpenCode.Protocol;
using OpenCode.Schema;

/// <summary>Owned embedded composition. No listener, managed-service registration or automatic recovery.</summary>
public sealed record SdkHostOptions
{
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public string? DatabasePath { get; init; }
    public string? RipgrepExecutable { get; init; }
    public string? FormatterBin { get; init; }
    public CodeModeLimits? CodeModeLimits { get; init; }
    public NormalizePromptImage? NormalizeImage { get; init; }
    public Func<LocationInfo, IPermissionEvaluationHook?>? PermissionHooks { get; init; }
    public Func<LocationInfo, IToolExecutionHooks?>? ToolHooks { get; init; }
    /// <summary>An injected store remains caller-owned. Default grants use this host's existing SQLite database.</summary>
    public IPermissionGrantStore? PermissionGrants { get; init; }
    public SessionRequestIdentity Identity { get; init; } = new("sdk", OpenCodeChannel.UserAgent + "/" + ApplicationBuild.Id);
}
