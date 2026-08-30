namespace OpenCode.Sdk.Tests;

using System.Text.Json;
using OpenCode.Core.Tools;
using OpenCode.Schema;

public class ToolTests
{
    [Fact]
    public async Task BuiltinTools_ReadWriteEditGrepGlob_WorkFaithfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"opencode_tool_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var registry = new ToolRegistry(new HttpClient());
            var dummyContext = new ToolContext(
                SessionId.Create(),
                new AgentId("build"),
                MessageId.Create(),
                "call_test",
                _ => Task.CompletedTask
            );

            // 1. WriteTool
            var testFile = Path.Combine(tempDir, "test.txt");
            var writeInput = JsonDocument.Parse($$"""{ "path": {{JsonSerializer.Serialize(testFile)}}, "content": "Line 1\nLine 2 TARGET\nLine 3" }""").RootElement;
            var writeResult = await registry.ExecuteAsync("write", writeInput, dummyContext);
            Assert.Contains("Successfully wrote", writeResult.Content);

            // 2. ReadTool
            var readInput = JsonDocument.Parse($$"""{ "path": {{JsonSerializer.Serialize(testFile)}} }""").RootElement;
            var readResult = await registry.ExecuteAsync("read", readInput, dummyContext);
            Assert.Contains("1: Line 1", readResult.Content);
            Assert.Contains("2: Line 2 TARGET", readResult.Content);

            // 3. EditTool
            var editInput = JsonDocument.Parse($$"""{ "path": {{JsonSerializer.Serialize(testFile)}}, "oldString": "Line 2 TARGET", "newString": "Line 2 REPLACED" }""").RootElement;
            var editResult = await registry.ExecuteAsync("edit", editInput, dummyContext);
            Assert.Contains("Successfully replaced", editResult.Content);

            // Read again to verify replacement
            var verifyRead = await registry.ExecuteAsync("read", readInput, dummyContext);
            Assert.Contains("2: Line 2 REPLACED", verifyRead.Content);

            // 4. GrepTool
            var grepInput = JsonDocument.Parse($$"""{ "path": {{JsonSerializer.Serialize(tempDir)}}, "pattern": "REPLACED" }""").RootElement;
            var grepResult = await registry.ExecuteAsync("grep", grepInput, dummyContext);
            Assert.Contains("Line 2 REPLACED", grepResult.Content);

            // 5. GlobTool
            var globInput = JsonDocument.Parse($$"""{ "path": {{JsonSerializer.Serialize(tempDir)}}, "pattern": "**/*.txt" }""").RootElement;
            var globResult = await registry.ExecuteAsync("glob", globInput, dummyContext);
            Assert.Contains("test.txt", globResult.Content);

            // 6. ShellTool
            var shellInput = JsonDocument.Parse("""{ "command": "echo SHELL_TOOL_OK" }""").RootElement;
            var shellResult = await registry.ExecuteAsync("shell", shellInput, dummyContext);
            Assert.Contains("SHELL_TOOL_OK", shellResult.Content);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
