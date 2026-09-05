namespace OpenCode.Core.Tools;

internal sealed partial class ShellSyntaxScanner
{
    private List<ScannedShellCommand> PosixCase(int depth)
    {
        _index += 4;
        Trivia();
        var commands = new List<ScannedShellCommand>();
        _ = Word(depth, commands); // Selector is data; only its substitutions are commands.
        Expect("in");
        while (true)
        {
            Trivia();
            if (Keyword() == "esac") { _index += 4; return commands; }
            if (_index >= source.Length) throw Unsupported("unterminated case statement");
            if (source[_index] == '(') _index++;
            while (true)
            {
                Trivia(false);
                _ = Word(depth, commands);
                Trivia(false);
                if (_index >= source.Length) throw Unsupported("unterminated case pattern");
                if (source[_index] == ')') { _index++; break; }
                if (source[_index++] != '|') throw Unsupported("case pattern requires '|' or ')'");
            }
            // Every arm is scanned, including fall-through arms. No glob matching
            // or selector propagation decides which branch receives permission.
            commands.AddRange(List(depth + 1, stops: [";;&", ";;", ";&", "esac"]));
            if (Keyword() == "esac") { _index += 4; return commands; }
            _index += source.AsSpan(_index).StartsWith(";;&", StringComparison.Ordinal) ? 3 : 2;
        }
    }

    private List<ScannedShellCommand> PowerShellSwitch(int depth)
    {
        _index += 6;
        Trivia();
        while (_index < source.Length && source[_index] == '-')
        {
            var start = _index++;
            while (_index < source.Length && char.IsAsciiLetter(source[_index])) _index++;
            var flag = source[start.._index];
            if (!new[] { "-exact", "-wildcard", "-regex", "-casesensitive" }.Contains(flag, StringComparer.OrdinalIgnoreCase))
                throw Unsupported("switch supports exact/wildcard/regex/case-sensitive flags, not file input or abbreviated flags");
            Trivia();
        }
        var commands = Group(depth, '(', true);
        Trivia();
        if (_index >= source.Length || source[_index++] != '{') throw Unsupported("switch requires a brace body");
        while (true)
        {
            Trivia();
            if (_index >= source.Length) throw Unsupported("unterminated switch body");
            if (source[_index] == '}') { _index++; return commands; }
            if (source[_index] == '{') commands.AddRange(Group(depth, '{'));
            else
            {
                // Literal/default labels are values, not executable names. A
                // computed label's parenthesized/substitution commands are scanned.
                _ = Word(depth, commands);
            }
            commands.AddRange(Group(depth, '{'));
            Trivia();
            if (_index < source.Length && source[_index] == ';') _index++;
        }
    }
}
