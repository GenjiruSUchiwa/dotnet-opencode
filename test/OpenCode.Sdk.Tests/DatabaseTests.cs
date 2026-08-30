namespace OpenCode.Sdk.Tests;

using OpenCode.Schema;

public class DatabaseTests
{
    [Fact]
    public async Task CanConnectToRealOpenCodeDbAndListSessions()
    {
        await using var client = await OpenCodeClient.CreateAsync();

        var sessions = await client.Sessions.ListSessionsAsync(limit: 10);
        Assert.NotNull(sessions);
        Assert.NotEmpty(sessions);

        var first = sessions[0];
        Assert.NotNull(first.Id.Value);
        Assert.StartsWith(SessionId.Prefix, first.Id.Value);
    }

    [Fact]
    public async Task CanCreateSessionInSharedDb()
    {
        await using var client = await OpenCodeClient.CreateAsync();

        var created = await client.Sessions.CreateSessionAsync(
            directory: Directory.GetCurrentDirectory(),
            title: "opencode-dotnet verification session"
        );

        Assert.NotNull(created);
        Assert.StartsWith(SessionId.Prefix, created.Id.Value);

        var list = await client.Sessions.ListSessionsAsync(limit: 5);
        Assert.Contains(list, s => s.Id == created.Id);
    }
}
