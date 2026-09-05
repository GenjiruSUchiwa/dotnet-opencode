namespace OpenCode.Core.Session;

/// <summary>ECMAScript trim semantics for source-generated titles and compaction summaries.</summary>
internal static class SessionText
{
    // Includes BOM, excludes NEL: .NET's default whitespace set differs from String.trim().
    private static readonly char[] Whitespace = "\u0009\u000A\u000B\u000C\u000D\u0020\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF".ToCharArray();

    internal static string Trim(string value) => value.Trim(Whitespace);
}
