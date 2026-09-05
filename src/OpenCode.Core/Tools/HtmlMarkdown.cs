namespace OpenCode.Core.Tools;

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

/// <summary>Source html-markdown renderer driven by an inert HTML parser, never regex-based HTML stripping.
/// AngleSharp's HTML5 tree repair can differ from htmlparser2 on malformed markup.</summary>
public static class HtmlMarkdown
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    private const int ContentBytes = MaximumBytes - 64 * 1024;
    private static readonly HashSet<string> Omitted = ["script", "style", "noscript", "iframe", "object", "embed", "meta", "link", "template"];
    private static readonly HashSet<string> TextOmitted = ["script", "style", "noscript", "iframe", "object", "embed"];
    private static readonly HashSet<string> Blocks = ["address", "article", "aside", "details", "dialog", "div", "dl", "fieldset", "figcaption", "figure", "footer", "form", "header", "main", "nav", "p", "section", "summary"];
    private static readonly char[] Whitespace = "\u0009\u000A\u000B\u000C\u000D\u0020\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF".ToCharArray();

    public static async Task<string> ConvertAsync(string html, bool plainText, CancellationToken ct = default)
    {
        // UTF-8 replacement decoding can expand byte count while retaining at most one UTF-16 unit per input byte.
        if (html.Length > MaximumBytes) throw new ToolExecutionException("HTML input exceeds the conversion limit.");
        // HtmlParser has no browsing loader or script engine; links, styles and images are never fetched.
        using var document = await new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocumentAsync(html, ct).ConfigureAwait(false);
        var renderer = plainText ? null : new Renderer();
        var text = new StringBuilder();
        var skip = 0;
        var templates = new Stack<IHtmlTemplateElement>();
        var node = document.FirstChild;
        while (node is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (node is IElement element)
            {
                if (renderer is not null) renderer.Open(element.LocalName.ToLowerInvariant(), element);
                else if (skip > 0 || TextOmitted.Contains(element.LocalName.ToLowerInvariant())) skip++;
            }
            else if (node is IText value)
            {
                if (renderer is not null) renderer.Text(value.Data);
                else if (skip == 0) text.Append(value.Data);
            }
            if (node is IHtmlTemplateElement template && template.Content.FirstChild is { } templateChild)
            { templates.Push(template); node = templateChild; continue; }
            if (node.FirstChild is { } child) { node = child; continue; }
            while (node is not null)
            {
                if (node is IElement closing)
                {
                    if (renderer is not null) renderer.Close(closing.LocalName.ToLowerInvariant());
                    else if (skip > 0) skip--;
                }
                if (node.NextSibling is { } sibling) { node = sibling; break; }
                node = node.Parent;
                if (templates.TryPeek(out var parentTemplate) && ReferenceEquals(node, parentTemplate.Content)) node = templates.Pop();
                if (ReferenceEquals(node, document)) { node = null; break; }
            }
        }
        ct.ThrowIfCancellationRequested();
        var result = renderer is null ? text.ToString().Trim(Whitespace) : renderer.Finish();
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private sealed record Chunk(string Text, bool Raw = false);
    private sealed record Link(string Href, string? Title);
    private sealed class Code(bool inline)
    {
        public readonly bool Inline = inline;
        public readonly StringBuilder Text = new();
        public string? Language;
    }
    private sealed class Marker(int index, int block, Marker? previous)
    {
        public readonly int Index = index;
        public readonly int Block = block;
        public readonly Marker? Previous = previous;
        public bool LeadingSpace;
    }
    private sealed class ListState(bool ordered, double next, ListState? previous)
    {
        public readonly bool Ordered = ordered;
        public double Next = next;
        public readonly ListState? Previous = previous;
    }
    private sealed record Item(string Indent, Item? Previous);
    private sealed class Table(int start, Table? previous)
    {
        public readonly int Start = start;
        public readonly Table? Previous = previous;
        public readonly List<List<string>> Rows = [];
        public List<string>? Row;
        public string? Caption;
        public bool Fallback;
    }
    private sealed class Details(bool open, Details? previous)
    {
        public readonly bool Open = open;
        public readonly Details? Previous = previous;
        public bool Summary;
    }
    private sealed class Frame
    {
        public bool Suppressed;
        public Link? Link;
        public Link? SuspendedLink;
        public Marker? Marker;
        public Code? Code;
        public Code? LinkCode;
        public Code? ResumedCode;
        public ListState? List;
        public Item? Item;
        public Table? Table;
        public int? Cell;
        public int? Caption;
        public Details? Details;
    }

    private sealed class Renderer
    {
        private readonly List<Chunk> _output = [];
        private readonly List<Frame> _stack = [];
        private bool _pendingSpace;
        private string _pendingIndent = "";
        private char _last;
        private int _quoteDepth;
        private bool _needsQuotePrefix;
        private int _blockCount;
        private int _depth;
        private bool _stopped;
        private int _bytes;
        private Code? _code;
        private Link? _link;
        private bool _linkOpen;
        private Marker? _marker;
        private ListState? _list;
        private Item? _item;
        private Table? _table;
        private int? _cell;
        private int _tableDepth;
        private int _fallbackSuppressedDepth;
        private int _fallbackOmittedDepth;
        private Details? _details;

        private void Append(string value, bool content = false, bool raw = false)
        {
            var remaining = (content ? ContentBytes : MaximumBytes) - _bytes;
            if (value.Length == 0 || remaining <= 0) return;
            var next = SliceBytes(value, remaining);
            if (next.Length == 0) return;
            _output.Add(new(next, raw));
            _bytes += Encoding.UTF8.GetByteCount(next);
            _last = next[^1];
        }

        private string Take(int start)
        {
            var value = string.Concat(_output.Skip(start).Select(chunk => chunk.Text));
            _output.RemoveRange(start, _output.Count - start);
            _bytes -= Encoding.UTF8.GetByteCount(value);
            return value;
        }

        private string QuotePrefix() => string.Concat(Enumerable.Repeat("> ", Math.Min(8, _quoteDepth)));
        private void PrefixQuote()
        {
            if (!_needsQuotePrefix || _quoteDepth == 0 || _cell is not null) return;
            Append(QuotePrefix());
            _needsQuotePrefix = false;
        }

        private void FlushSpace()
        {
            if (!_pendingSpace) return;
            if (_marker is { } marker && _output.Count == marker.Index + 1 && _last is not (' ' or '\n') && !_output[marker.Index].Raw)
            {
                _output[marker.Index] = new(" " + _output[marker.Index].Text);
                _bytes++;
                marker.LeadingSpace = true;
                _pendingSpace = false;
                return;
            }
            if (_last is not ('\0' or '\n' or ' ')) Append(" ");
            _pendingSpace = false;
        }

        private void Inline(string value, bool open = false)
        {
            if (open) FlushSpace();
            PrefixQuote();
            if (_pendingIndent.Length > 0) { Append(_pendingIndent); _pendingIndent = ""; }
            if (_link is not null && !_linkOpen) { Append("["); _linkOpen = true; }
            Append(value);
        }

        private void Block()
        {
            if (_link is not null && _linkOpen) { Append(LinkClose(_link)); _linkOpen = false; }
            _pendingSpace = false;
            Append("\n\n");
            _blockCount++;
            _needsQuotePrefix = _quoteDepth > 0;
            _pendingIndent = _item?.Indent ?? "";
        }

        private void SuspendLink(Frame frame)
        {
            if (_link is null) return;
            frame.SuspendedLink = _link;
            Block();
            _link = null;
            _linkOpen = false;
        }

        public void Text(string value)
        {
            if (_stopped)
            {
                if (_fallbackSuppressedDepth != 0 || _fallbackOmittedDepth != 0) return;
            }
            else if (_stack.LastOrDefault()?.Suppressed == true) return;
            WriteText(value);
        }

        private void WriteText(string value)
        {
            if (_code is not null) { _code.Text.Append(value); return; }
            var index = 0;
            while (index < value.Length)
            {
                var start = index;
                var space = IsSpace(value[index]);
                while (index < value.Length && IsSpace(value[index]) == space) index++;
                if (space) { _pendingSpace = true; continue; }
                FlushSpace();
                PrefixQuote();
                if (_pendingIndent.Length > 0) { Append(_pendingIndent); _pendingIndent = ""; }
                if (_link is not null && !_linkOpen) { Append("["); _linkOpen = true; }
                Append(EscapeText(value[start..index]), true);
            }
        }

        public void Open(string name, IElement element)
        {
            _depth++;
            if (_depth > 10_000)
            {
                if (_stack.LastOrDefault()?.Suppressed == true) _fallbackSuppressedDepth = _stack.FindLastIndex(frame => !frame.Suppressed) + 2;
                _code = null;
                _stopped = true;
            }
            if (_stopped)
            {
                if (Omitted.Contains(name)) _fallbackOmittedDepth++;
                else _pendingSpace = true;
                return;
            }
            var frame = new Frame { Suppressed = _stack.LastOrDefault()?.Suppressed == true || Omitted.Contains(name) };
            if (element.HasAttribute("hidden") || string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase) || name == "head" ||
                _details is { Open: false, Summary: false } && name != "summary") frame.Suppressed = true;
            _stack.Add(frame);
            if (frame.Suppressed) return;
            if (_code is { Inline: false })
            {
                if (name == "br") _code.Text.Append('\n');
                if (name == "code" && element.GetAttribute("class") is { } classes)
                {
                    var match = Regex.Match(classes, @"(?:language-|lang-)(?<language>[^\s]+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
                    if (match.Success) _code.Language = match.Groups["language"].Value;
                }
                return;
            }
            if (name == "details") { frame.Details = new(element.HasAttribute("open"), _details); _details = frame.Details; Block(); return; }
            if (name == "summary") { if (_details is not null) _details.Summary = true; Block(); return; }
            if (name == "pre") { SuspendLink(frame); frame.Code = new(false); _code = frame.Code; return; }
            if (name == "code") { if (_code?.Inline == true) return; frame.Code = new(true); _code = frame.Code; return; }
            if (Heading(name)) { Block(); Inline(new string('#', name[1] - '0') + " "); return; }
            if (Blocks.Contains(name)) { SuspendLink(frame); if (name != "p" || _last != ' ') Block(); return; }
            if (name == "br") { _pendingSpace = false; Inline("  \n"); _needsQuotePrefix = _quoteDepth > 0; return; }
            if (name == "hr") { Block(); Inline("---"); Block(); return; }
            if (InlineMarker(name) is { } marker)
            {
                Inline(marker, true);
                frame.Marker = new(_output.Count - 1, _blockCount, _marker);
                _marker = frame.Marker;
                return;
            }
            if (name == "a")
            {
                if (_link is not null && _code?.Inline == true)
                {
                    var parent = _stack.LastOrDefault(candidate => ReferenceEquals(candidate.Link, _link) && ReferenceEquals(candidate.LinkCode, _code));
                    if (parent is not null)
                    {
                        FinishCode(_code);
                        if (_linkOpen) Append(LinkClose(_link));
                        _code = parent.ResumedCode;
                        parent.Link = null;
                        parent.LinkCode = null;
                        _link = null;
                        _linkOpen = false;
                    }
                }
                if (_code?.Inline == true)
                {
                    frame.ResumedCode = _code;
                    if (_code.Text.Length > 0) FinishCode(_code);
                    _code.Text.Clear();
                    _code = null;
                    frame.Link = new(element.GetAttribute("href") ?? "", element.GetAttribute("title"));
                    _link = frame.Link;
                    _linkOpen = true;
                    Inline("[", true);
                    frame.LinkCode = new(true);
                    _code = frame.LinkCode;
                    return;
                }
                if (_link is not null)
                {
                    if (_linkOpen) Append(LinkClose(_link));
                    var parent = _stack.LastOrDefault(candidate => ReferenceEquals(candidate.Link, _link));
                    if (parent is not null) parent.Link = null;
                    _link = null;
                    _linkOpen = false;
                }
                frame.Link = new(element.GetAttribute("href") ?? "", element.GetAttribute("title"));
                _link = frame.Link;
                _linkOpen = true;
                Inline("[", true);
                return;
            }
            if (name == "img")
            {
                var alt = Replace(element.GetAttribute("alt") ?? "", @"([\\\]])", @"\$1");
                var close = "](" + Destination(element.GetAttribute("src") ?? "") + Title(element.GetAttribute("title")) + ")";
                Inline("![" + SliceBytes(alt, Math.Max(0, ContentBytes - _bytes - Encoding.UTF8.GetByteCount("![" + close))) + close, true);
                return;
            }
            if (name == "blockquote") { SuspendLink(frame); Block(); _quoteDepth++; _needsQuotePrefix = true; return; }
            if (name is "ul" or "ol")
            {
                SuspendLink(frame);
                frame.List = new(name == "ol", ParseInteger(element.GetAttribute("start"), 1), _list);
                _list = frame.List;
                Block();
                return;
            }
            if (name == "li")
            {
                Block();
                var ordinal = ParseInteger(element.GetAttribute("value"), double.NaN);
                if (_list?.Ordered == true && !double.IsNaN(ordinal)) _list.Next = ordinal;
                var bullet = _list?.Ordered == true ? Number(_list.Next++) + "." : "-";
                var previousIndent = _item?.Indent ?? "";
                var prefix = previousIndent[..Math.Min(24, previousIndent.Length)] + bullet + " ";
                frame.Item = new(new string(' ', prefix.Length), _item);
                _item = frame.Item;
                _pendingIndent = "";
                Inline(prefix);
                return;
            }
            if (name == "table")
            {
                SuspendLink(frame);
                if (++_tableDepth == 1) { Block(); frame.Table = new(_output.Count, _table); _table = frame.Table; }
                else _pendingSpace = true;
                return;
            }
            if (name == "tr") { if (_tableDepth != 1) _pendingSpace = true; else if (_table is not null) _table.Row = []; return; }
            if (name is "th" or "td")
            {
                if (_tableDepth != 1) { _pendingSpace = true; return; }
                if (_table is not null && (!string.IsNullOrEmpty(element.GetAttribute("colspan")) || !string.IsNullOrEmpty(element.GetAttribute("rowspan")))) _table.Fallback = true;
                frame.Cell = _output.Count;
                _cell = frame.Cell;
                return;
            }
            if (name == "caption") { frame.Caption = _output.Count; return; }
            if (name == "dt") { Block(); Inline("**"); return; }
            if (name == "dd") Inline("\n: ");
        }

        public void Close(string name)
        {
            _depth--;
            if (_stopped)
            {
                if (_fallbackOmittedDepth > 0 && Omitted.Contains(name)) _fallbackOmittedDepth--;
                if (_fallbackSuppressedDepth > 0 && _depth < _fallbackSuppressedDepth) _fallbackSuppressedDepth = 0;
                return;
            }
            if (_stack.Count == 0) return;
            var frame = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            if (frame.Suppressed) return;
            if (frame.LinkCode is { } linkCode)
            {
                if (!ReferenceEquals(_link, frame.Link) || !_linkOpen)
                {
                    frame.ResumedCode!.Text.Append(linkCode.Text);
                    _code = frame.ResumedCode;
                    if (ReferenceEquals(_link, frame.Link)) _link = null;
                    _linkOpen = false;
                    return;
                }
                _code = null;
                FinishCode(linkCode);
                if (frame.Link is not null && _linkOpen) Append(LinkClose(frame.Link));
                _link = null;
                _linkOpen = false;
                _code = frame.ResumedCode;
                return;
            }
            if (_code is { Inline: false } && frame.Code is null) return;
            if (frame.Code is { } code)
            {
                _code = null;
                FinishCode(code);
                if (frame.SuspendedLink is not null) _link = frame.SuspendedLink;
                return;
            }
            if (name == "summary") { if (_details is not null) _details.Summary = false; Block(); return; }
            if (name == "details") { _details = frame.Details?.Previous; Block(); return; }
            if (name == "dt") { Inline("**"); return; }
            if (name == "dd") { Block(); return; }
            if (InlineMarker(name) is { } marker)
            {
                var trailing = _pendingSpace;
                _pendingSpace = false;
                if (frame.Marker is not null) _marker = frame.Marker.Previous;
                if (frame.Marker is { } opened && (opened.Block != _blockCount || _output.Count == opened.Index + 1))
                {
                    if (opened.Index >= 0) _output[opened.Index] = new("");
                    _pendingSpace = trailing || opened.LeadingSpace;
                    return;
                }
                Inline(marker);
                _pendingSpace = trailing || frame.Marker?.LeadingSpace == true;
                return;
            }
            if (name == "a")
            {
                if (frame.Link is not null && (ReferenceEquals(_link, frame.Link) || _link is null))
                {
                    _link = frame.Link;
                    if (_linkOpen || _last is not ('\0' or '\n')) Append(LinkClose(_link));
                    _linkOpen = false;
                    _link = null;
                }
                return;
            }
            if (Heading(name) || Blocks.Contains(name)) { Block(); if (frame.SuspendedLink is not null) _link = frame.SuspendedLink; return; }
            if (name == "blockquote") { _quoteDepth--; Block(); if (frame.SuspendedLink is not null) _link = frame.SuspendedLink; return; }
            if (name == "li") { _item = frame.Item?.Previous; Block(); return; }
            if (name is "ul" or "ol") { _list = frame.List?.Previous; Block(); if (frame.SuspendedLink is not null) _link = frame.SuspendedLink; return; }
            if (name is "th" or "td" && _tableDepth == 1)
            {
                _cell = null;
                if (frame.Cell is { } start) _table?.Row?.Add(EscapePipes(Replace(Take(start), @"[\t\r\n ]+", " ").Trim(Whitespace)));
                return;
            }
            if (name == "tr") { if (_tableDepth != 1) return; if (_table?.Row is { } row) _table.Rows.Add(row); if (_table is not null) _table.Row = null; _pendingSpace = true; return; }
            if (name == "caption" && frame.Caption is { } caption && _table is not null) { _table.Caption = Replace(Take(caption), @"[\t\r\n ]+", " ").Trim(Whitespace); return; }
            if (name == "table")
            {
                if (--_tableDepth != 0) { _pendingSpace = true; return; }
                var table = frame.Table;
                _table = table?.Previous;
                if (table is not null)
                {
                    var loose = Replace(Take(table.Start), @"[\t\r\n ]+", " ").Trim(Whitespace);
                    var width = table.Rows.FirstOrDefault()?.Count ?? 0;
                    if (loose.Length > 0) { Append(loose); Block(); }
                    if (!string.IsNullOrEmpty(table.Caption)) { Append(table.Caption); Block(); }
                    if (!table.Fallback && width > 0 && table.Rows.All(row => row.Count == width))
                    {
                        var prefix = (_quoteDepth > 0 ? QuotePrefix() : "") + _pendingIndent;
                        _pendingIndent = "";
                        Append(prefix + "| " + string.Join(" | ", table.Rows[0]) + " |\n" + prefix + "|" + string.Concat(Enumerable.Repeat(" --- |", width)));
                        foreach (var row in table.Rows.Skip(1)) Append("\n" + prefix + "| " + string.Join(" | ", row) + " |");
                    }
                    else
                    {
                        for (var index = 0; index < table.Rows.Count; index++) { if (index > 0) Block(); Append(string.Join(" | ", table.Rows[index])); }
                    }
                }
                Block();
                if (frame.SuspendedLink is not null) _link = frame.SuspendedLink;
            }
        }

        private void FinishCode(Code code)
        {
            var value = code.Text.ToString();
            if (code.Inline && value.Length == 0) return;
            var ticks = Longest(value, '`');
            var tildes = Longest(value, '~');
            if (code.Inline)
            {
                var fence = new string('`', Math.Max(1, ticks + 1));
                var padding = (value.StartsWith(' ') || value.EndsWith(' ')) && value.Any(character => character != ' ') ? " " : "";
                FlushSpace();
                PrefixQuote();
                Append(fence + padding + SliceBytes(value, Math.Max(0, ContentBytes - _bytes - 2 * (fence.Length + padding.Length))) + padding + fence, raw: true);
                return;
            }
            if (_cell is not null) { WriteText(value); return; }
            var character = ticks <= tildes ? '`' : '~';
            var blockFence = new string(character, Math.Max(3, (character == '`' ? ticks : tildes) + 1));
            Block();
            var prefix = blockFence + code.Language + "\n";
            var quote = _quoteDepth > 0 ? QuotePrefix() : "";
            while (true)
            {
                var candidate = prefix + value + (value.EndsWith('\n') ? "" : "\n") + blockFence;
                if (quote.Length > 0) candidate = quote + candidate.Replace("\n", "\n" + quote, StringComparison.Ordinal);
                var bytes = Encoding.UTF8.GetByteCount(candidate);
                if (_bytes + bytes <= ContentBytes) { Append(candidate, raw: true); Block(); return; }
                var next = SliceBytes(value, Math.Max(0, Encoding.UTF8.GetByteCount(value) - (bytes - Math.Max(0, ContentBytes - _bytes))));
                // A fence/language wrapper alone can exceed the budget. Never reproduce a non-progress loop.
                if (next.Length == value.Length) throw new ToolExecutionException("HTML code block exceeds the bounded conversion budget.");
                value = next;
            }
        }

        public string Finish()
        {
            var normalized = new StringBuilder();
            var pending = new StringBuilder();
            foreach (var chunk in _output)
            {
                if (!chunk.Raw) { pending.Append(chunk.Text); continue; }
                Flush();
                normalized.Append(chunk.Text);
            }
            Flush();
            return SliceBytes(normalized.ToString().Trim(Whitespace), MaximumBytes);

            void Flush()
            {
                if (pending.Length == 0) return;
                var text = Regex.Replace(pending.ToString(), @"[ \t]+\n", match => match.Value.StartsWith("  ", StringComparison.Ordinal) ? "  \n" : "\n", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
                var lines = Replace(text, @"\n{3,}", "\n\n").Split('\n');
                for (var index = 1; index + 1 < lines.Length; index++)
                {
                    if (lines[index].Length > 0) continue;
                    var before = QuoteLength(lines[index - 1]);
                    var after = QuoteLength(lines[index + 1]);
                    if (before > 0 && after > 0 && before != after) lines[index] = string.Concat(Enumerable.Repeat("> ", Math.Min(before, after))).TrimEnd();
                }
                normalized.Append(string.Join('\n', lines));
                pending.Clear();
            }
        }
    }

    private static string SliceBytes(string value, int maximum)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximum) return value;
        var bytes = 0;
        var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximum) break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return value[..length];
    }
    private static bool IsSpace(char value) => value is '\t' or '\n' or '\f' or '\r' or ' ';
    private static bool Heading(string name) => name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6';
    private static string? InlineMarker(string name) => name switch { "strong" or "b" => "**", "em" or "i" => "*", "s" or "strike" or "del" => "~~", _ => null };
    private static string Replace(string value, string pattern, string replacement) => Regex.Replace(value, pattern, replacement, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static string Destination(string value) => Replace(Replace(value, @"([\\()])", @"\$1"), @"[\t\n\r ]+", "%20");
    private static string Title(string? value) => string.IsNullOrEmpty(value) ? "" : " \"" + Replace(Replace(value, @"[\t\n\r ]+", " ").Trim(Whitespace), "([\\\\\"])", @"\$1") + "\"";
    private static string LinkClose(Link link) => "](" + Destination(link.Href) + Title(link.Title) + ")";
    private static string EscapeText(string value)
    {
        var result = new StringBuilder();
        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits])) digits++;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if ("\\`*_[]<>|~".Contains(character) || index == 0 && character is '#' or '+' or '-' || index == digits && digits > 0 && character == '.') result.Append('\\');
            result.Append(character);
        }
        return result.ToString();
    }
    private static string EscapePipes(string value)
    {
        var output = new StringBuilder();
        for (var index = 0; index < value.Length; index++) { if (value[index] == '|' && (index == 0 || value[index - 1] != '\\')) output.Append('\\'); output.Append(value[index]); }
        return output.ToString();
    }
    private static int Longest(string value, char marker)
    {
        var best = 0;
        var current = 0;
        foreach (var character in value) { current = character == marker ? current + 1 : 0; best = Math.Max(best, current); }
        return best;
    }
    private static int QuoteLength(string value)
    {
        var length = 0;
        while (length * 2 + 1 < value.Length && value[length * 2] == '>' && value[length * 2 + 1] == ' ') length++;
        return length;
    }
    private static double ParseInteger(string? value, double fallback)
    {
        if (value is null) return fallback;
        var text = value.TrimStart(Whitespace);
        var negative = text.StartsWith('-');
        if (text.StartsWith('-') || text.StartsWith('+')) text = text[1..];
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex) text = text[2..];
        var end = 0;
        while (end < text.Length && (hex ? char.IsAsciiHexDigit(text[end]) : char.IsAsciiDigit(text[end]))) end++;
        if (end == 0) return fallback;
        var digits = text[..end].TrimStart('0');
        var number = digits.Length == 0 ? 0 : !hex
            ? double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture)
            : digits.Length > 256 ? double.PositiveInfinity
            : (double)BigInteger.Parse("0" + digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return negative ? -number : number;
    }
    private static string Number(double value)
    {
        if (value == 0) return "0";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var index = text.IndexOf('E');
        if (index < 0) return text;
        var exponent = int.Parse(text[(index + 1)..], CultureInfo.InvariantCulture);
        if (exponent is < 0 or >= 21) return text[..index] + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
        var mantissa = text[..index];
        var sign = mantissa.StartsWith('-') ? "-" : "";
        var digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal);
        return sign + digits.PadRight(exponent + 1, '0');
    }
}
