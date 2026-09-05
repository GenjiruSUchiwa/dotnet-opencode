namespace OpenCode.Cli.Tui.Components;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Blazor.Clipboard;

public partial class OpenCodeApp
{
    [Parameter] public Func<ClipboardReadRequest, CancellationToken, Task<ClipboardReadResult>>? ReadPromptClipboard { get; set; }
    private bool _clipboardReading;

    private async Task PasteClipboard()
    {
        if (_clipboardReading || PromptBlocked || ReadPromptClipboard is null) return;
        _clipboardReading = true;
        var origin = (_tabs.Selected, _sessionId, _draftRevision, _cursor, _selectionAnchor, ShellMode);
        var location = SelectionLocation;
        var client = ReadSessionClient?.Invoke();
        bool Changed() => origin != (_tabs.Selected, _sessionId, _draftRevision, _cursor, _selectionAnchor, ShellMode)
            || location != SelectionLocation || client != ReadSessionClient?.Invoke() || PromptBlocked;
        try
        {
            // Called by the explicit prompt.paste action on the renderer dispatcher only.
            var result = await ReadPromptClipboard(new(["image/png", "text/uri-list", "text/plain"]), _configurationLifetime.Token);
            if (Changed()) return;
            if (result.Status != ClipboardReadStatus.Read)
            {
                _inputError = result.Status switch
                {
                    ClipboardReadStatus.Empty => "The clipboard has no available PNG, URI list, or text representation.",
                    ClipboardReadStatus.Unsupported => "Clipboard reading is unsupported by the current host backend.",
                    ClipboardReadStatus.Cancelled => "Clipboard paste was cancelled.",
                    ClipboardReadStatus.TimedOut => "Clipboard reading timed out.",
                    ClipboardReadStatus.LimitExceeded => "Clipboard data exceeds the host read limit.",
                    _ => "Clipboard reading failed."
                };
                if (result.Error is { } error) _inputError += " " + error.Message + (error.NativeCode is { } code ? $" (native code {code})" : "");
                return;
            }
            var representation = result.Representation ?? throw new InvalidOperationException("Clipboard read returned no representation.");
            if (representation.MimeType == "text/plain") { Paste(representation.ReadText()); return; }
            if (representation.MimeType == "image/png")
            {
                if (ShellMode) { _inputError = "Images cannot be pasted into a shell command. Exit shell mode to attach the image."; return; }
                var uri = "data:image/png;base64," + Convert.ToBase64String(representation.Bytes.Span);
                var files = CapturePromptInput(_input).Files ?? [];
                var label = files.FirstOrDefault(file => file.Uri == uri && file.Name == "clipboard" && file.Description is null && file.Mention?.Text.Length > 0)?.Mention?.Text;
                var highest = files.Select(file => Regex.Match(file.Mention?.Text ?? "", @"^\[Image (?<number>\d+)\]$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                    .Where(match => match.Success).Select(match => int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
                    .DefaultIfEmpty(0).Max();
                InsertClipboardParts([(label ?? $"[Image {checked(highest + 1)}]", new PromptInputFileAttachment(uri, "clipboard"))]);
                return;
            }
            if (representation.MimeType != "text/uri-list")
                throw new InvalidOperationException($"Unsupported clipboard representation: {representation.MimeType}.");
            var uris = representation.ReadUris();
            if (uris.Count == 0) { _inputError = "The clipboard URI list is empty."; return; }
            if (ShellMode && uris.Any(uri => uri.IsFile))
            { _inputError = "File references are not shell arguments. Exit shell mode to attach files."; return; }
            var basis = new Uri(FileReference.FileUri(location.Directory, "").TrimEnd('/') + "/");
            var parts = new List<(string Text, PromptInputFileAttachment? File)>();
            foreach (var uri in uris)
            {
                if (!uri.IsFile) { parts.Add((uri.AbsoluteUri, null)); continue; }
                if (client is null) throw new InvalidOperationException("Connect to the server before attaching clipboard files.");
                if (!basis.IsBaseOf(uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                    throw new InvalidOperationException("Clipboard file is outside the selected server Location. No local file was opened or uploaded.");
                var relative = Uri.UnescapeDataString(basis.MakeRelativeUri(uri).OriginalString).TrimEnd('/');
                var slash = relative.LastIndexOf('/');
                var response = await client.ListFilesAsync(slash < 0 ? null : relative[..slash], location.Directory, location.WorkspaceId?.Value, _configurationLifetime.Token);
                if (Changed()) return;
                if (new LocationRef(response.Location.Directory, response.Location.WorkspaceId) != location)
                    throw new InvalidOperationException("Clipboard file lookup resolved a different Location.");
                var entry = response.Data.FirstOrDefault(entry => new Uri(FileReference.FileUri(response.Location.Directory, entry.Path)) == uri);
                if (entry is null) throw new InvalidOperationException("Clipboard file is not present in the selected server Location. No local file was opened or uploaded.");
                parts.Add(("@" + entry.Path, new(uri.AbsoluteUri, entry.Path)));
            }
            if (Changed()) return;
            InsertClipboardParts(parts);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (!Changed()) _inputError = SessionClientAdapter.Describe(exception); }
        finally { _clipboardReading = false; _dirty = true; }
    }

    private void InsertClipboardParts(IReadOnlyList<(string Text, PromptInputFileAttachment? File)> parts)
    {
        parts = parts.Select(part => (TerminalTextEditing.NormalizePaste(part.Text), part.File)).ToArray();
        var start = HasSelection ? Math.Min(_cursor, _selectionAnchor!.Value) : _cursor;
        var length = HasSelection ? Math.Abs(_cursor - _selectionAnchor!.Value) : 0;
        var text = string.Concat(parts.Select(part => part.Text + " "));
        var offset = AttachmentEdits.Offset(_input, start, MeasureMentionElement);
        var files = new List<PromptInputFileAttachment>();
        foreach (var part in parts)
        {
            var end = offset + AttachmentEdits.Offset(part.Text, part.Text.Length, MeasureMentionElement);
            if (part.File is { } file) files.Add(file with { Mention = new(offset, end, part.Text) });
            offset = end + MeasureMentionElement(" ");
        }
        // The entire list is one edit: rejected entries never leave a partial paste.
        if (!ReplacePromptRange(start, length, text)) return;
        var input = CapturePromptInput(_input);
        RestorePromptAttachments(EditorKey, input with { Files = (input.Files ?? []).Concat(files).ToArray() });
        _dismissedReferenceText = _input;
        _dismissedCommandInput = _input;
        _dirty = true;
    }
}
