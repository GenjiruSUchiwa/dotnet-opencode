namespace OpenCode.Core.Tools;

internal sealed partial class ShellSyntaxScanner
{
    private static readonly HashSet<string> PosixDeclarations = new(StringComparer.Ordinal)
        { "declare", "typeset", "export", "readonly", "local", "unset", "unsetenv", "return", "exit", "break", "continue" };

    // Only an unquoted, unescaped scalar name before '=' is an assignment word.
    // No values are propagated to later command names or directory arguments.
    private static bool PosixAssignment(string raw)
    {
        var index = raw.IndexOf('=');
        if (index <= 0) return false;
        var name = raw[..index];
        if (name.EndsWith('+')) name = name[..^1];
        return Identifier(name);
    }

    private static bool Identifier(string name) => name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private string Keyword()
    {
        var end = _index;
        while (end < source.Length && (char.IsAsciiLetterOrDigit(source[end]) || source[end] == '_')) end++;
        if (end < source.Length && !" \t\r\n;(){}<>|&".Contains(source[end])) return "";
        var text = source[_index..end];
        return powershell ? text.ToLowerInvariant() : text;
    }

    private void Trivia(bool lines = true)
    {
        while (_index < source.Length)
        {
            Checkpoint();
            if (lines && source[_index] == '\n' && _pendingHereDocuments > 0)
                throw Unsupported("nested newline before a pending here-document body is not classified");
            if (source[_index] is ' ' or '\t' || (lines && source[_index] is '\r' or '\n')) { _index++; continue; }
            if (lines && source[_index] == '#')
            {
                while (_index < source.Length && source[_index] is not ('\r' or '\n')) _index++;
                continue;
            }
            break;
        }
    }

    private void Expect(string keyword)
    {
        Trivia();
        if (Keyword() != keyword) throw Unsupported($"expected '{keyword}' in shell structure");
        _index += keyword.Length;
    }

    private List<ScannedShellCommand> Group(int depth, char opener, bool required = false)
    {
        Trivia();
        if (_index >= source.Length || source[_index] != opener) throw Unsupported($"expected '{opener}' in shell structure");
        if (!powershell && opener == '{' && !char.IsWhiteSpace(Peek(1))) throw Unsupported("POSIX brace opener requires a token boundary");
        _index++;
        return List(depth + 1, opener == '(' ? ')' : '}', requireStatement: required);
    }

    private List<ScannedShellCommand>? PosixStatement(int depth)
    {
        if (depth > 32) throw Unsupported("shell structure nesting exceeds 32 levels");
        if (source.AsSpan(_index).StartsWith("((", StringComparison.Ordinal)) return Arithmetic(depth);
        var keyword = Keyword();
        if (keyword == "case") return PosixCase(depth);
        if (source[_index] == '(' || (source[_index] == '{' && char.IsWhiteSpace(Peek(1))))
            return Group(depth, source[_index], true);
        if (source[_index] == '!' && char.IsWhiteSpace(Peek(1)))
        {
            _index++;
            Trivia();
            if (_index >= source.Length) throw Unsupported("missing command after '!'");
            return PosixStatement(depth + 1); // The following simple command remains a permission resource.
        }
        if (keyword == "if")
        {
            _index += 2;
            var commands = List(depth + 1, stops: ["then"], requireStatement: true);
            Expect("then");
            commands.AddRange(List(depth + 1, stops: ["elif", "else", "fi"], requireStatement: true));
            while (Keyword() == "elif")
            {
                _index += 4;
                commands.AddRange(List(depth + 1, stops: ["then"], requireStatement: true));
                Expect("then");
                commands.AddRange(List(depth + 1, stops: ["elif", "else", "fi"], requireStatement: true));
            }
            if (Keyword() == "else")
            {
                _index += 4;
                commands.AddRange(List(depth + 1, stops: ["fi"], requireStatement: true));
            }
            Expect("fi");
            return commands;
        }
        if (keyword is "while" or "until")
        {
            _index += keyword.Length;
            var commands = List(depth + 1, stops: ["do"], requireStatement: true);
            Expect("do");
            commands.AddRange(List(depth + 1, stops: ["done"], requireStatement: true));
            Expect("done");
            return commands;
        }
        if (keyword is "for" or "select")
        {
            _index += keyword.Length;
            Trivia(false);
            var commands = new List<ScannedShellCommand>();
            if (keyword == "for" && source.AsSpan(_index).StartsWith("((", StringComparison.Ordinal))
            {
                commands.AddRange(Arithmetic(depth, header: true));
                Trivia(false);
                if (_index < source.Length && source[_index] == ';') _index++;
                Expect("do");
                commands.AddRange(List(depth + 1, stops: ["done"], requireStatement: true));
                Expect("done");
                return commands;
            }
            var name = Word(depth, commands);
            if (!Identifier(name.Raw)) throw Unsupported("for/select requires a scalar variable name or a supported arithmetic for header");
            Trivia(false);
            if (Keyword() == "in")
            {
                _index += 2;
                Trivia(false);
                while (_index < source.Length && source[_index] is not (';' or '\n'))
                {
                    _ = Word(depth, commands); // Header words are values, but every substitution is scanned.
                    Trivia(false);
                }
            }
            if (_index >= source.Length || source[_index] is not (';' or '\n')) throw Unsupported("missing for/select header separator");
            _index++;
            Expect("do");
            commands.AddRange(List(depth + 1, stops: ["done"], requireStatement: true));
            Expect("done");
            return commands;
        }
        // Source scans function bodies even if no invocation is statically visible.
        var saved = _index;
        if (keyword == "function")
        {
            _index += keyword.Length;
            Trivia(false);
            keyword = Keyword();
            if (!Identifier(keyword)) throw Unsupported("function requires a literal identifier");
        }
        if (Identifier(keyword))
        {
            _index += keyword.Length;
            Trivia(false);
            var parentheses = _index < source.Length && source[_index] == '(';
            if (parentheses)
            {
                _index++;
                Trivia(false);
                if (_index >= source.Length || source[_index++] != ')') throw Unsupported("unsupported function parameter syntax");
            }
            if (parentheses || source.AsSpan(saved).StartsWith("function ", StringComparison.Ordinal) ||
                source.AsSpan(saved).StartsWith("function\t", StringComparison.Ordinal))
            {
                Trivia();
                if (_index >= source.Length || source[_index] is not ('{' or '(')) throw Unsupported("function requires a brace or subshell body");
                return Group(depth, source[_index], true);
            }
        }
        _index = saved;
        return null;
    }
}
