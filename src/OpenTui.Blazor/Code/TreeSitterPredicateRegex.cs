namespace OpenTui.Blazor.Code;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>A declared subset of flagless JavaScript RegExp, used only on query-selected node text.</summary>
internal static class TreeSitterPredicateRegex
{
    private const string Space = @"\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
    private const string Word = "A-Za-z0-9_";

    internal static Regex Compile(string source)
    {
        var pattern = new StringBuilder();
        var inClass = false;
        var classStart = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (character == '\\')
            {
                if (++index == source.Length) throw Unsupported("trailing escape");
                var escape = source[index];
                if (escape is 's' or 'w' or 'd' or 'S' or 'W' or 'D')
                {
                    var negative = char.IsAsciiLetterUpper(escape);
                    if (inClass && negative) throw Unsupported("complement escapes inside character classes");
                    if (inClass && (index - 2 >= classStart && source[index - 2] == '-' ||
                        index + 1 < source.Length && source[index + 1] == '-'))
                        throw Unsupported("character-class shorthand range endpoints");
                    var body = char.ToLowerInvariant(escape) switch { 's' => Space, 'w' => Word, _ => "0-9" };
                    pattern.Append(inClass ? body : "[" + (negative ? "^" : "") + body + "]");
                    continue;
                }
                if (escape is 'x' or 'u')
                {
                    var length = escape == 'x' ? 2 : 4;
                    if (source.Length - index - 1 < length || source.AsSpan(index + 1, length).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
                        throw Unsupported("non-fixed hexadecimal escape");
                    pattern.Append(escape == 'x' ? "\\u00" : "\\u").Append(source.AsSpan(index + 1, length));
                    index += length;
                    continue;
                }
                if (escape == '0' && (index + 1 == source.Length || !char.IsAsciiDigit(source[index + 1])))
                {
                    pattern.Append(@"\u0000");
                    continue;
                }
                if (escape == 'b' && inClass) { pattern.Append(@"\u0008"); continue; }
                if (escape is 'f' or 'n' or 'r' or 't' or 'v') { pattern.Append('\\').Append(escape); continue; }
                if (char.IsAsciiLetterOrDigit(escape)) throw Unsupported("backreferences, word boundaries, or identity/control escapes");
                pattern.Append('\\').Append(escape);
                continue;
            }
            if (character == '[')
            {
                if (inClass) throw Unsupported("nested character classes");
                inClass = true;
                classStart = index + 1;
                pattern.Append(character);
                continue;
            }
            if (character == ']' && inClass)
            {
                if (index == classStart || index == classStart + 1 && source[classStart] == '^') throw Unsupported("empty character classes");
                inClass = false;
                pattern.Append(character);
                continue;
            }
            if (inClass) { pattern.Append(character); continue; }
            if (character == '(' && index + 1 < source.Length && source[index + 1] == '?' &&
                (index + 2 == source.Length || source[index + 2] != ':'))
                throw Unsupported("lookaround, named groups, or inline flags");
            // Unlike .NET's default $, flagless JS $ does not match before a final LF.
            if (character == '$') { pattern.Append(@"\z"); continue; }
            // JS dot excludes all four ECMAScript line terminators, not only LF.
            if (character == '.') { pattern.Append(@"[^\r\n\u2028\u2029]"); continue; }
            if (character == ']') { pattern.Append(@"\]"); continue; }
            if (character == '{')
            {
                var end = source.IndexOf('}', index + 1);
                if (end < 0 || end == index + 1 || !char.IsAsciiDigit(source[index + 1])) throw Unsupported("literal or malformed quantifier braces");
                var comma = false;
                for (var digit = index + 1; digit < end; digit++)
                {
                    if (source[digit] == ',' && !comma) { comma = true; continue; }
                    if (!char.IsAsciiDigit(source[digit])) throw Unsupported("malformed quantifier");
                }
                pattern.Append(source.AsSpan(index, end - index + 1));
                index = end;
                continue;
            }
            if (character == '}') throw Unsupported("literal quantifier braces");
            pattern.Append(character);
        }
        if (inClass) throw Unsupported("unterminated character class");
        return new Regex(pattern.ToString(), RegexOptions.ECMAScript | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static NotSupportedException Unsupported(string feature) =>
        new($"Tree-sitter #match? uses an unsupported JavaScript RegExp construct: {feature}. No approximate match was used.");
}
