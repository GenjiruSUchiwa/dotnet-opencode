namespace OpenTui.Blazor.Code;

using System.Text;
using OpenTui.Native;

/// <summary>Source capture-to-style projection; this does not parse or guess tokens.</summary>
public static class CodeProjection
{
    private readonly record struct Boundary(int Offset, bool Start, int Index);

    public static CodeDocument Create(CodeOptions options, IReadOnlyList<CodeCapture> captures)
    {
        var rules = new Dictionary<string, NativeSyntaxRule>(StringComparer.Ordinal);
        foreach (var rule in options.SyntaxRules ?? []) rules[rule.Scope] = rule;
        var sourceLines = options.Content.Select((character, index) => (character, index)).Where(item => item.character == '\n').Select(item => item.index).ToArray();
        var boundaries = captures.SelectMany((capture, index) =>
        {
            if (capture.Start < 0 || capture.End < capture.Start || capture.End > options.Content.Length)
                throw new ArgumentException("A syntax capture is outside its source snapshot.", nameof(captures));
            if (SplitsSurrogate(capture.Start) || SplitsSurrogate(capture.End)) throw new ArgumentException("A syntax capture splits a Unicode scalar.", nameof(captures));
            return capture.Start == capture.End ? [] : new[] { new Boundary(capture.Start, true, index), new Boundary(capture.End, false, index) };
        }).OrderBy(item => item.Offset).ThenBy(item => item.Start).ToArray();
        var text = new StringBuilder();
        var spans = new List<CodeStyleSpan>();
        var styles = new Dictionary<(NativeRgba? Foreground, NativeRgba? Background, uint Attributes), string>();
        var outputRules = new List<NativeSyntaxRule>(rules.Values);
        var lines = new List<int> { 0 };
        var active = new List<int>();
        var offset = 0;
        var fallback = Resolve(options.BaseHighlight) ?? Resolve("default");
        foreach (var boundary in boundaries)
        {
            if (offset < boundary.Offset)
            {
                var conceal = options.Conceal ? active.Select(index => captures[index]).FirstOrDefault(capture =>
                    capture.Metadata?.HasConceal == true || capture.Scope == "conceal" || capture.Scope.StartsWith("conceal.", StringComparison.Ordinal)) : null;
                if (conceal is not null)
                {
                    var replacement = conceal.Metadata?.HasConceal == true ? conceal.Metadata.Conceal ?? "" : conceal.Scope == "conceal.with.space" ? " " : "";
                    Append(replacement, offset, Resolve("default"));
                }
                else
                {
                    var injection = captures.Any(capture => capture.Metadata?.ContainsInjection == true && offset >= capture.Start && offset < capture.End);
                    var style = Resolve(options.BaseHighlight);
                    foreach (var index in active.Where(index => !injection || captures[index].Metadata?.IsInjection == true || captures[index].Scope != "markup.raw.block")
                        .OrderBy(index => captures[index].Scope.Count(character => character == '.')).ThenBy(index => index))
                    {
                        if (Resolve(captures[index].Scope) is not { } next) continue;
                        style = new("", next.Foreground ?? style?.Foreground, next.Background ?? style?.Background,
                            ((style?.Attributes ?? 0) & ~next.AttributeMask) | (next.Attributes & next.AttributeMask));
                    }
                    Append(options.Content[offset..boundary.Offset], offset, style ?? fallback);
                }
            }
            if (boundary.Start) active.Add(boundary.Index);
            else
            {
                active.Remove(boundary.Index);
                var capture = captures[boundary.Index];
                if (options.Conceal && boundary.Offset < options.Content.Length)
                {
                    var next = options.Content[boundary.Offset];
                    if (capture.Metadata?.ConcealLines == true && next == '\n' ||
                        capture.Metadata?.HasConceal == true && next == ' ' && (capture.Metadata.Conceal == " " ||
                            capture.Metadata.Conceal == "" && capture.Scope == "conceal" && !capture.Metadata.IsInjection))
                    { offset = boundary.Offset + 1; continue; }
                }
            }
            offset = Math.Max(offset, boundary.Offset);
        }
        if (offset < options.Content.Length) Append(options.Content[offset..], offset, fallback);
        return new(text.ToString(), Array.AsReadOnly(outputRules.ToArray()), Array.AsReadOnly(spans.ToArray()), Array.AsReadOnly(lines.ToArray()));

        bool SplitsSurrogate(int position) => position > 0 && position < options.Content.Length && char.IsHighSurrogate(options.Content[position - 1]) && char.IsLowSurrogate(options.Content[position]);
        NativeSyntaxRule? Resolve(string? scope)
        {
            if (scope is null) return null;
            if (rules.TryGetValue(scope, out var rule)) return rule;
            var dot = scope.IndexOf('.');
            return dot > 0 ? rules.GetValueOrDefault(scope[..dot]) : null;
        }
        int SourceLine(int position)
        {
            var found = Array.BinarySearch(sourceLines, position);
            return found >= 0 ? found : ~found;
        }
        void Append(string value, int sourceOffset, NativeSyntaxRule? style)
        {
            if (value.Length == 0) return;
            var start = text.Length;
            if (start == 0 || text[^1] == '\n') lines[^1] = SourceLine(sourceOffset);
            text.Append(value);
            for (var index = 0; index < value.Length; index++)
                if (value[index] == '\n') lines.Add(SourceLine(Math.Min(options.Content.Length, sourceOffset + index + 1)));
            if (style is null) return;
            var key = (style.Foreground, style.Background, style.Attributes);
            if (!styles.TryGetValue(key, out var name))
            {
                name = "__code_segment_" + styles.Count;
                styles.Add(key, name);
                outputRules.Add(style with { Scope = name });
            }
            spans.Add(new(start, text.Length, name));
        }
    }
}
