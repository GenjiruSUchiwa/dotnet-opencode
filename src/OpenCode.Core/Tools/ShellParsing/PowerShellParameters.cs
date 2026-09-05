namespace OpenCode.Core.Tools;

internal sealed partial class ShellSyntaxScanner
{
    private List<ScannedShellCommand> PowerShellParameters(int depth)
    {
        if (depth >= 32) throw Unsupported("parameter declaration nesting exceeds 32 levels");
        var commands = new List<ScannedShellCommand>();
        Trivia();
        if (_index >= source.Length || source[_index++] != '(') throw Unsupported("parameters require a parenthesized list");
        Trivia();
        if (_index < source.Length && source[_index] == ')') { _index++; return commands; }
        while (true)
        {
            Checkpoint();
            while (_index < source.Length && source[_index] == '[')
            {
                PowerShellType(depth + 1, commands, attributes: true);
                Trivia();
            }
            PowerShellVariable();
            Trivia();
            if (_index < source.Length && source[_index] == '=')
            {
                _index++;
                Trivia();
                var value = PowerShellScalar(depth + 1, commaStops: true);
                if (value is null) throw Unsupported("parameter defaults require expressions; parenthesize command pipelines");
                commands.AddRange(value);
                Trivia();
            }
            if (_index >= source.Length) throw Unsupported("unterminated parameter list");
            if (source[_index] == ')') { _index++; return commands; }
            if (source[_index++] != ',') throw Unsupported("parameter list requires a comma");
            Trivia();
        }
    }
}
