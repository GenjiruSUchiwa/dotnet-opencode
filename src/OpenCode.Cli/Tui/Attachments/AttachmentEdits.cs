namespace OpenCode.Cli.Tui.Attachments;

using System.Globalization;
using OpenCode.Schema;

/// <summary>UTF-16 editing boundaries with source-compatible native-display mention offsets.</summary>
public static class AttachmentEdits
{
    public static int Offset(string text, int index, Func<string, int> width)
    {
        var result = 0;
        var elements = StringInfo.GetTextElementEnumerator(text[..index]);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            result += element == "\n" ? 1 : width(element);
        }
        return result;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Preserve the existing display-position diagnostic shown by the attachment editor.")]
    public static int Index(string text, double offset, Func<string, int> width)
    {
        if (offset < 0 || Math.Truncate(offset) != offset) throw new ArgumentException("Attachment offset must be a nonnegative display position.");
        var position = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            if (position == offset) return elements.ElementIndex;
            var element = elements.GetTextElement();
            position += element == "\n" ? 1 : width(element);
        }
        if (position == offset) return text.Length;
        throw new ArgumentException("Attachment offset is not a display-text boundary.");
    }

    public static PromptInput Replace(PromptInput input, int start, int length, string inserted, Func<string, int> width)
    {
        return new AttachmentTextMarks(input).Replace(start, length, inserted, width).Input;
    }
}
