namespace OpenCode.Core.Tools;

using System.Text;

internal sealed record ShellWord(string Raw, string Value, bool Literal, bool Expression = false, bool Assignment = false);
internal sealed record ScannedShellCommand(string Resource, IReadOnlyList<ShellWord> Words, bool Redirected, int? PrefixWordCount = null, bool ExactGrant = false);

/// <summary>Managed, non-executing scanner for the advertised bounded grammar. Opaque syntax is an error,
/// never permission to execute the unscanned source. Raw command spans are retained for permission rules.</summary>
internal sealed partial class ShellSyntaxScanner(string source, bool powershell, CancellationToken ct)
{
    private int _index;
    private int _pendingHereDocuments;
    private static readonly HashSet<string> Compound = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "then", "elif", "elseif", "else", "fi", "for", "foreach", "while", "until", "do", "done", "case", "esac",
        "select", "function", "filter", "switch", "try", "catch", "finally", "begin", "process", "end", "clean",
        "param", "trap", "class", "enum", "data", "dynamicparam", "using", "time", "coproc", "!", "[[", "]]",
        "declare", "typeset", "export", "readonly", "local", "unset", "unsetenv", "return", "throw", "exit", "break", "continue"
    };

    public IReadOnlyList<ScannedShellCommand> Scan()
    {
        if (source.Length is 0 or > 65536) throw Unsupported("command must contain 1–65536 characters");
        Checkpoint(source.Length);
        // These characters have shell-specific token/quote meanings that this grammar does not approximate.
        if (source.Any(character => character == '\0' || (!powershell && character == '\r') || (char.IsControl(character) && character is not ('\t' or '\n' or '\r')) ||
            (powershell && (character is >= '\u2013' and <= '\u201e' or '\ufeff' || character == '\u0085'))))
            throw Unsupported("unsupported control characters or PowerShell Unicode syntax; POSIX commands require LF newlines");
        return List(0, requireStatement: true);
    }

    private List<ScannedShellCommand> List(int depth, char close = '\0', string[]? stops = null, bool requireStatement = false)
    {
        if (depth > 32) throw Unsupported("command substitution nesting exceeds 32 levels");
        var commands = new List<ScannedShellCommand>();
        var words = new List<ShellWord>();
        var nested = new List<ScannedShellCommand>();
        var start = _index;
        var end = _index;
        var redirected = false;
        int? prefixWordCount = null;
        var invocation = false;
        var required = false;
        var compound = false;
        var populated = false;
        var heredocs = new List<HereDocument>();
        while (_index < source.Length)
        {
            Checkpoint();
            var character = source[_index];
            if (character == '\n' && _pendingHereDocuments > heredocs.Count)
                throw Unsupported("nested newline before an outer here-document body is not classified");
            if (character is ' ' or '\t') { _index++; continue; }
            if (character == (powershell ? '`' : '\\') && Peek(1) is '\r' or '\n')
            {
                _index += 2;
                if (source[_index - 1] == '\r' && _index < source.Length && source[_index] == '\n') _index++;
                continue;
            }
            if (character == '#')
            {
                while (_index < source.Length && source[_index] is not ('\r' or '\n')) _index++;
                continue;
            }
            if ((close != '\0' && character == close) ||
                (stops is not null && stops.Any(stop => char.IsAsciiLetter(stop[0])
                    ? words.Count == 0 && !compound && Keyword() == stop
                    : source.AsSpan(_index).StartsWith(stop, StringComparison.Ordinal))))
            {
                if (heredocs.Count > 0) throw Unsupported("here-document body must follow a newline before closing the structure");
                if (!powershell && close == '}' && (words.Count > 0 || compound)) throw Unsupported("brace groups require a separator before '}'");
                Finish();
                if (required || (requireStatement && !populated)) throw Unsupported("missing statement in shell structure");
                if (character == close) _index++;
                return commands;
            }
            if (words.Count == 0 && !redirected && !invocation && !compound)
            {
                var statementStart = _index;
                var parsed = powershell ? PowerShellStatement(depth) : PosixStatement(depth);
                if (parsed is not null)
                {
                    start = statementStart;
                    end = _index;
                    commands.AddRange(parsed);
                    populated = compound = true;
                    required = false;
                    continue;
                }
                if (_index >= source.Length) throw Unsupported("missing command after shell prefix");
                character = source[_index];
            }
            if (character is ';' or '\r' or '\n' or '|' or '&' && !(!powershell && character == '&' && Peek(1) == '>'))
            {
                // PowerShell's call operator accepts a literal executable, including a quoted path.
                if (powershell && character == '&' && Peek(1) != '&' && words.Count == 0 && !redirected && !invocation && !compound)
                { start = _index; invocation = true; _index++; continue; }
                var separator = character.ToString();
                if ((character is '|' or '&') && Peek(1) == character) separator += character;
                if (separator == "&") throw Unsupported("background '&' jobs are not supported");
                if (character == '|' && Peek(1) == '&') throw Unsupported("use an explicit stderr redirect instead of '|&'");
                if (character == ';' && Peek(1) is ';' or '&') throw Unsupported("compound command separator");
                if (words.Count == 0 && !redirected && !compound)
                {
                    if (invocation || character is ';' or '|' or '&') throw Unsupported("empty command between operators");
                    // A newline can continue a pipeline/conditional list.
                }
                else
                {
                    Finish();
                    required = separator is "|" or "||" or "&&";
                }
                _index += separator.Length;
                if (character == '\r' && _index < source.Length && source[_index] == '\n') _index++;
                if (character == '\n' && heredocs.Count > 0)
                {
                    foreach (var document in heredocs)
                    {
                        var body = HereDocumentBody(document);
                        _pendingHereDocuments--;
                        var owner = document.Owner ?? throw Unsupported("here-document has no classified permission resource");
                        commands[owner] = commands[owner] with { Resource = source[document.ResourceStart.._index].Trim() };
                        if (!document.Quoted) commands.AddRange(Rescan(body).TextExpansions(depth + 1));
                    }
                    heredocs.Clear();
                }
                start = end = _index;
                continue;
            }
            if (words.Count == 0 && !redirected && !invocation && !compound) start = _index;
            if (TryRedirect(depth, nested, heredocs))
            {
                if (!powershell && prefixWordCount is null && words.Any(word => !word.Assignment && !PosixAssignment(word.Raw))) prefixWordCount = words.Count;
                redirected = true;
                end = _index;
                required = false;
                continue;
            }
            if (compound) throw Unsupported("expected a separator or redirect after compound statement or scalar expression");
            if (powershell && words.Count > 0 && PowerShellStopToken() is { } stop)
            {
                words.Add(stop);
                var markerEnd = _index;
                var tail = PowerShellStopTail();
                if (tail is not null) words.Add(tail);
                end = tail is null ? markerEnd : _index;
                required = false;
                continue;
            }
            words.Add(Word(depth, nested));
            end = _index;
            required = false;
        }
        if (heredocs.Count > 0) throw Unsupported("missing here-document body");
        if (close != '\0' || stops is not null) throw Unsupported("unterminated shell structure");
        Finish();
        if (required || (requireStatement && !populated)) throw Unsupported("missing shell statement");
        return commands;

        void Finish()
        {
            if (compound)
            {
                if (redirected) AddResource([]);
                commands.AddRange(nested);
                nested.Clear();
                compound = redirected = false;
                prefixWordCount = null;
                return;
            }
            if (words.Count == 0)
            {
                if (invocation) throw Unsupported("a literal command name is required after the invocation operator");
                if (redirected)
                {
                    if (powershell) throw Unsupported("PowerShell redirection requires a command or scalar expression");
                    populated = true;
                    AddResource([]);
                    commands.AddRange(nested);
                    nested.Clear();
                    redirected = false;
                    prefixWordCount = null;
                }
                return;
            }
            populated = true;
            var leading = powershell ? 0 : words.TakeWhile(word => word.Assignment || PosixAssignment(word.Raw)).Count();
            if (leading == words.Count)
            {
                if (redirected) AddResource([]);
                commands.AddRange(nested);
                words.Clear(); nested.Clear();
                redirected = false;
                prefixWordCount = null;
                return;
            }
            var selected = words.Skip(leading).ToArray();
            var head = selected[0];
            if (!head.Literal || head.Value.Length == 0 || head.Value.Contains('=') ||
                (!powershell && head.Value != "[" && head.Value.IndexOfAny(['*', '?', '[', ']', '~']) >= 0))
                throw Unsupported("dynamic command names are not supported; assigned values are not propagated");
            if (Compound.Contains(head.Raw, powershell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) &&
                !(!powershell && PosixDeclarations.Contains(head.Raw)) && !(powershell && head.Value.Equals("foreach", StringComparison.OrdinalIgnoreCase)))
                throw Unsupported($"compound syntax '{head.Value}' is not supported in this position");
            if (powershell && !invocation && (head.Raw[0] is '\'' or '"' || char.IsAsciiDigit(head.Raw[0]) || head.Raw[0] is '+' or '-' or '!'))
                throw Unsupported("PowerShell expressions require a command; use '&' for a quoted executable");
            AddResource(selected);
            commands.AddRange(nested);
            words.Clear();
            nested.Clear();
            redirected = invocation = false;
            prefixWordCount = null;
        }

        void AddResource(IReadOnlyList<ShellWord> selected)
        {
            foreach (var document in heredocs.Where(document => document.Owner is null))
            {
                document.Owner = commands.Count;
                document.ResourceStart = start;
            }
            commands.Add(new(source[start..end].Trim(), selected, redirected,
                selected.Count > 0 && prefixWordCount is { } count ? count - (words.Count - selected.Count) : null,
                selected.Any(word => word.Expression)));
        }
    }

    private ShellWord Word(int depth, List<ScannedShellCommand> nested, bool quotedParameter = false, bool expressionString = false)
    {
        var start = _index;
        if (powershell && _index < source.Length && source[_index] == '@' && Peek(1) is '\'' or '"')
            return PowerShellHereString(depth, nested);
        if (powershell && !expressionString && _index < source.Length &&
            (source[_index] is '(' or '{' or '[' || (source[_index] == '@' && Peek(1) is '(' or '{')))
        {
            if (PowerShellAtom(depth, nested)) nested.Add(new(source[start.._index].Trim(), [], false));
            return new(source[start.._index], source[start.._index], false, true);
        }
        if (powershell && !expressionString && _index < source.Length && source[_index] == '@')
        {
            _index++;
            var name = _index;
            while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_')) _index++;
            if (!Identifier(source[name.._index]) || (_index < source.Length && !" \t\r\n;|&<>)}".Contains(source[_index])))
                throw Unsupported("splatting requires a simple named variable");
            nested.Add(new(source[start.._index], [], false));
            return new(source[start.._index], source[start.._index], false, true);
        }
        var value = new StringBuilder();
        var literal = true;
        var assignment = false;
        var quote = '\0';
        while (_index < source.Length)
        {
            Checkpoint();
            var character = source[_index];
            if (quotedParameter && character == '\'') throw Unsupported("single quotes in a double-quoted parameter fallback are not classified");
            if (quote == '\'' || (quote == '"' && character == '"'))
            {
                if (character == quote)
                {
                    if (powershell && Peek(1) == quote) { value.Append(quote); _index += 2; continue; }
                    quote = '\0';
                    if (powershell && expressionString)
                    {
                        _index++;
                        return new(source[start.._index], value.ToString(), literal);
                    }
                    if (powershell && _index + 1 < source.Length && source[_index + 1] is '.' or '[')
                    {
                        _index++;
                        if (!PowerShellSuffix(depth, nested)) throw Unsupported("unclassified quoted expression suffix");
                        nested.Add(new(source[start.._index], [], false));
                        return new(source[start.._index], source[start.._index], false, true);
                    }
                    if (powershell && _index + 1 < source.Length && !" \t\r\n;|&<>)}".Contains(source[_index + 1]))
                        throw Unsupported("PowerShell concatenated quoting is not supported");
                }
                else value.Append(character);
                _index++;
                continue;
            }
            if (quote == '\0')
            {
                if (!powershell && character == '[' && Identifier(source[start.._index]) && SubscriptAssignmentAhead())
                {
                    var subscript = _index++;
                    ArrayKey(depth + 1, nested);
                    if (_index < source.Length && source[_index] == '+') _index++;
                    if (_index >= source.Length || source[_index++] != '=') throw Unsupported("unclassified subscript assignment boundary");
                    value.Append(source.AsSpan(subscript, _index - subscript));
                    assignment = true;
                    literal = false;
                    continue;
                }
                if (!powershell && character == '(' && source[start.._index].EndsWith('=') &&
                    (assignment || PosixAssignment(source[start.._index])))
                {
                    if (assignment) throw Unsupported("array-valued element assignments are not classified");
                    ArrayConstruction(depth, nested);
                    if (_index < source.Length && !" \t\n;|&<>)}".Contains(source[_index])) throw Unsupported("array construction cannot be concatenated with another word");
                    return new(source[start.._index], source[start.._index], false, true, true);
                }
                if (!powershell && character is '<' or '>' && Peek(1) == '(')
                {
                    var expansion = _index;
                    _index += 2;
                    nested.AddRange(CommandSubstitution(depth + 1));
                    value.Append(source.AsSpan(expansion, _index - expansion));
                    literal = false;
                    continue;
                }
                if (powershell && character is '<' or '>' && _index > start)
                    throw Unsupported("PowerShell redirects must start at a token boundary");
                if (character is ' ' or '\t' or '\r' or '\n' or ';' or '|' or '&' or '<' or '>' or ')' || character == '}') break;
                if (character is '(' or '{' or '}' || (powershell && character is '[' or ']' or ',' or '@'))
                    throw Unsupported("unclassified group/token concatenation, array, here-string or splatting syntax");
                if (char.IsWhiteSpace(character)) throw Unsupported("non-ASCII shell whitespace");
                if (character is '\'' or '"')
                {
                    if (powershell && _index != start) throw Unsupported("PowerShell concatenated quoting is not supported");
                    quote = character; _index++; continue;
                }
            }
            if (character == (powershell ? '`' : '\\'))
            {
                if (_index + 1 >= source.Length) throw Unsupported("unterminated escape");
                var escaped = source[_index + 1];
                // POSIX double quotes preserve backslashes before non-special characters.
                if (!powershell && quote == '"' && !"$`\"\\\n\r".Contains(escaped))
                { value.Append(character); _index++; continue; }
                _index += 2;
                if (escaped is '\r' or '\n')
                {
                    if (powershell) throw Unsupported("PowerShell line continuation is supported only between tokens, not inside words or strings");
                    if (escaped == '\r' && _index < source.Length && source[_index] == '\n') _index++;
                    continue;
                }
                if (powershell && escaped == 'u' && _index < source.Length && source[_index] == '{')
                    throw Unsupported("PowerShell Unicode escapes are not supported");
                value.Append(powershell ? escaped switch
                {
                    '0' => '\0', 'a' => '\a', 'b' => '\b', 'e' => '\u001b', 'f' => '\f',
                    'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', _ => escaped
                } : escaped);
                continue;
            }
            if (!powershell && character == '`')
            {
                var expansion = _index;
                nested.AddRange(Backtick(depth));
                value.Append(source.AsSpan(expansion, _index - expansion));
                literal = false;
                continue;
            }
            if (character == '$')
            {
                var expansion = _index;
                if (Peek(1) == '(')
                {
                    if (!powershell && Peek(2) == '(') nested.AddRange(Arithmetic(depth));
                    else
                    {
                        _index += 2;
                        nested.AddRange(CommandSubstitution(depth + 1));
                    }
                }
                else
                {
                    if (!powershell && Peek(1) == '[')
                    {
                        nested.AddRange(Arithmetic(depth));
                        value.Append(source.AsSpan(expansion, _index - expansion));
                        literal = false;
                        continue;
                    }
                    if (!powershell && Peek(1) == '{')
                    {
                        ParameterExpansion(depth, nested, quote == '"' || quotedParameter);
                        value.Append(source.AsSpan(expansion, _index - expansion));
                        literal = false;
                        continue;
                    }
                    if (powershell && quote == '\0')
                    {
                        PowerShellVariable();
                        if (PowerShellSuffix(depth, nested))
                        {
                            nested.Add(new(source[start.._index], [], false));
                            return new(source[start.._index], source[start.._index], false, true);
                        }
                        value.Append(source.AsSpan(expansion, _index - expansion));
                        literal = false;
                        continue;
                    }
                    _index++;
                    if (_index < source.Length && source[_index] == '{')
                    {
                        _index++;
                        var name = _index;
                        while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_' || (powershell && source[_index] == ':'))) _index++;
                        if (_index == name || _index == source.Length || source[_index] != '}')
                            throw Unsupported("only simple variable expansion is supported");
                        _index++;
                    }
                    else
                    {
                        var name = _index;
                        while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_' || (powershell && source[_index] == ':'))) _index++;
                        if (_index == name)
                        {
                            if (_index < source.Length && (source[_index] is '?' or '$' or '#' || (!powershell && source[_index] is '@' or '*'))) _index++;
                            else throw Unsupported("unsupported dollar expression; quote literal '$' with single quotes");
                        }
                    }
                }
                if (powershell && quote == '\0' && PowerShellSuffix(depth, nested))
                {
                    nested.Add(new(source[start.._index], [], false));
                    return new(source[start.._index], source[start.._index], false, true);
                }
                value.Append(source.AsSpan(expansion, _index - expansion));
                literal = false;
                continue;
            }
            value.Append(character);
            _index++;
        }
        if (quote != '\0') throw Unsupported("unterminated quote");
        if (_index == start) throw Unsupported("unexpected shell token");
        return new(source[start.._index], value.ToString(), literal, assignment, assignment);
    }

    private bool TryRedirect(int depth, List<ScannedShellCommand> nested, List<HereDocument> heredocs)
    {
        var start = _index;
        if (!powershell && source[_index] is '<' or '>' && Peek(1) == '(') return false;
        var combined = !powershell && source[_index] == '&' && Peek(1) == '>';
        if (combined) _index++;
        while (_index < source.Length && char.IsAsciiDigit(source[_index])) _index++;
        var descriptor = source[start.._index];
        if (powershell && descriptor.Length == 0 && _index < source.Length && source[_index] == '*') { descriptor = "*"; _index++; }
        if (_index == source.Length || source[_index] is not ('<' or '>')) { _index = start; return false; }
        var kind = source[_index++];
        if (powershell && (kind == '<' || (descriptor.Length > 0 && descriptor != "*" && descriptor is not ("1" or "2" or "3" or "4" or "5" or "6"))))
            throw Unsupported("unsupported PowerShell stream redirect");
        var append = _index < source.Length && source[_index] == kind;
        if (append) _index++;
        var special = !powershell && !append && !combined && _index < source.Length &&
            ((kind == '<' && source[_index] == '>') || (kind == '>' && source[_index] == '|'));
        if (special) _index++;
        if (kind == '<' && append)
        {
            if (_index < source.Length && source[_index] == '<')
            {
                _index++;
                while (_index < source.Length && source[_index] is ' ' or '\t') _index++;
                if (_index == source.Length || source[_index] == '#') throw Unsupported("missing here-string word");
                _ = Word(depth, nested);
                return true;
            }
            var tabs = _index < source.Length && source[_index] == '-';
            if (tabs) _index++;
            heredocs.Add(HereDocumentDelimiter(tabs));
            _pendingHereDocuments++;
            return true;
        }
        if (_index < source.Length && source[_index] == '&')
        {
            if (append || combined || special || (powershell && descriptor is not ("2" or "3" or "4" or "5" or "6" or "*")))
                throw Unsupported("invalid stream redirect source");
            _index++;
            var target = _index;
            while (_index < source.Length && char.IsAsciiDigit(source[_index])) _index++;
            if (!powershell && _index == target && _index < source.Length && source[_index] == '-') _index++;
            if (_index == target || (powershell && source[target.._index] != "1")) throw Unsupported("invalid stream redirect target");
            if (_index < source.Length && !" \t\r\n;|&<> )}".Contains(source[_index])) throw Unsupported("invalid stream redirect boundary");
            return true;
        }
        if (_index < source.Length && source[_index] is '<' or '>' or '|' or '(') throw Unsupported("unsupported redirect syntax");
        while (_index < source.Length && source[_index] is ' ' or '\t') _index++;
        if (_index == source.Length || source[_index] == '#') throw Unsupported("missing redirect target");
        var targetWord = Word(depth, nested);
        if (powershell && targetWord.Value == "--%" && targetWord.Raw[0] is not ('\'' or '"'))
            throw Unsupported("stop-parsing markers in redirect targets are not classified");
        return true;
    }

    private char Peek(int distance) => _index + distance < source.Length ? source[_index + distance] : '\0';
    private static ToolExecutionException Unsupported(string reason) => new($"Shell scanner cannot analyze command: {reason}. No command was started.");
}
