namespace OpenCode.Protocol.Groups;

/// <summary>
/// 1:1 port of FileSystemGroup from packages/protocol/src/groups/fs.ts
/// </summary>
public static class FsEndpoints
{
    public const string Read = "/api/fs/read/{*path}";
    public const string List = "/api/fs/list";
    public const string Find = "/api/fs/find";
}
