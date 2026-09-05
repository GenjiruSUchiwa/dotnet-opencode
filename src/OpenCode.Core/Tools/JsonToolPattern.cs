namespace OpenCode.Core.Tools;

using Acornima;
using System.Text.RegularExpressions;

/// <summary>Flagless ECMAScript pattern assertions, matching the source JSON Schema importer.
/// Acornima adapts anchors, character classes, escapes, and dot semantics; raw .NET regex
/// syntax/ECMAScript mode alone is not equivalent. Only Boolean matching is exposed.</summary>
internal static class JsonToolPattern
{
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    internal static Regex Compile(string pattern, string path)
    {
        // Acornima documents differences for self/forward references and captures inside
        // repeated groups. Reject backreference-like escapes, even inside a class, rather
        // than allow those differences to affect whether an assertion succeeds.
        var characterClass = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '\\')
            {
                if (++index < pattern.Length && (pattern[index] is >= '1' and <= '9' or 'k'))
                    throw new NotSupportedException($"Unsupported tool schema pattern at {path}: backreference-like escapes are not supported.");
                continue;
            }
            if (character == '[' && !characterClass) { characterClass = true; continue; }
            if (character == ']' && characterClass) { characterClass = false; continue; }
            if (characterClass || character != '(' || index + 1 >= pattern.Length || pattern[index + 1] != '?') continue;
            // The JSON Schema source has no flags. Scoped modifiers could enable case
            // folding, which the adapter documents as non-equivalent between JS and .NET.
            // Ordinary/noncapturing groups, lookarounds, and named captures remain eligible
            // for Acornima's syntax and conversion validation.
            if (index + 2 >= pattern.Length || pattern[index + 2] is not (':' or '=' or '!' or '<'))
                throw new NotSupportedException($"Unsupported tool schema pattern at {path}: inline modifiers or unsupported group syntax.");
        }

        // Acornima 1.7.0 is already pinned by Core. Its public adapter is deprecated
        // for the next major version, but replacing it with raw .NET regex changes
        // semantics. Keep this compatibility dependency isolated for that upgrade.
#pragma warning disable CS0618 // Existing pinned ECMAScript adapter; do not replace with a non-equivalent .NET pattern.
        try
        {
            // Existing Core dependency; no JS engine, program evaluation, or new package.
            // Empty flags preserve source new RegExp(pattern).test(value) semantics:
            // case-sensitive, UTF-16, unanchored unless the pattern itself supplies anchors.
            return Tokenizer.AdaptRegExp(pattern, "", false, MatchTimeout, true).Regex
                ?? throw new NotSupportedException($"Unsupported tool schema pattern at {path}: no equivalent regex is available.");
        }
        catch (Exception error) when (error is SyntaxErrorException or RegExpConversionErrorException)
        {
            throw new NotSupportedException($"Invalid or unsupported ECMAScript tool schema pattern at {path}.", error);
        }
#pragma warning restore CS0618
    }
}
