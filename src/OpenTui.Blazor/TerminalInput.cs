namespace OpenTui.Blazor;

using System.Globalization;
using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Transport;

// Windows supplies decoded console keys. Unix supplies bytes to FeedUnixBytes;
// both use this one VT/paste/focus/mouse state machine.
internal sealed partial class TerminalInput(
    Action<ConsoleKeyInfo> key,
    Action<string> paste,
    Action<string> response,
    Action<bool>? focus = null,
    Action<string>? unknownResponse = null,
    Func<bool>? cursorReportExpected = null,
    Action<string>? pasteRejected = null,
    Action<TerminalPointerInput>? pointer = null,
    Action<string>? inputRejected = null,
    Action<TerminalKeyInput>? richKey = null,
    Func<string, bool>? consumeResponse = null, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private enum State { Ground, Escape, Csi, Ss3, ControlString, Mouse }
    private const string PasteEnd = "\x1b[201~";
    private readonly char[] _sequence = new char[4096];
    private readonly TextSequenceBuffer _paste = new();
    private readonly long _startupDeadline = (clock ?? TimeProvider.System).GetTimestampMilliseconds() + 5000;
    private State _state;
    private int _length;
    private int _pasteMatch;
    private int _mouseRemaining;
    private bool _pasting;
    private bool _pasteTooLarge;
    private bool _pasteInvalidEncoding;
    private bool _discarding;
    private bool _sawEscape;
    private bool _osc;
    private long _lastInput;
    private readonly SequenceBuffer _inputBytes = new();
    private bool _altSequence;
    private bool _unixInput;
    public string? LastRejectedInput { get; private set; }
    public long RejectedInputCount { get; private set; }
    public TerminalKeyInput? LastKeyInput { get; private set; }
    public long SuppressedKeyReleaseCount { get; private set; }

    /// <summary>Incremental UTF-8 input. Raw X10 coordinate bytes are identified by
    /// the existing parser state, not by a second escape interpreter.</summary>
    public void FeedUnixBytes(ReadOnlySpan<byte> bytes)
    {
        _unixInput = true;
        _lastInput = _clock.GetTimestampMilliseconds();
        Span<char> characters = stackalloc char[2];
        Span<byte> scalar = stackalloc byte[4];
        _inputBytes.Append(bytes);
        while (_inputBytes.Length > 0)
        {
            var sequence = _inputBytes.Sequence;
            if (_state == State.Mouse && !_pasting)
            {
                var reader = new SequenceReader<byte>(sequence);
                reader.TryRead(out var value);
                _inputBytes.Consume(1);
                FeedCore(new ConsoleKeyInfo((char)value, ConsoleKey.None, false, false, false), raw: true);
                continue;
            }
            var length = (int)Math.Min(4, sequence.Length);
            sequence.Slice(0, length).CopyTo(scalar);
            var status = Rune.DecodeFromUtf8(scalar[..length], out var rune, out var consumed);
            if (status == OperationStatus.NeedMoreData) break;
            _inputBytes.Consume(consumed);
            if (status == OperationStatus.InvalidData)
            {
                if (_pasting) { _pasteInvalidEncoding = true; _paste.Clear(); }
                Reject("Invalid UTF-8 terminal input was discarded; legacy eight-bit Meta encoding is unsupported.");
                continue;
            }
            var characterCount = rune.EncodeToUtf16(characters);
            var alt = _state == State.Escape && !_pasting;
            if (characterCount == 2 && !_pasting && _state is State.Ground or State.Escape)
            {
                var extraEscape = _altSequence;
                _state = State.Ground; _length = 0; _altSequence = false;
                EmitRaw(rune, alt, extraEscape);
            }
            else for (var index = 0; index < characterCount; index++)
                FeedCore(new ConsoleKeyInfo(characters[index], ConsoleKey.None, false, index > 0 && alt, false), raw: true);
        }
    }

    public void CompleteUnixInput()
    {
        if (_inputBytes.Length > 0) Reject("Terminal input ended with incomplete UTF-8; no replacement key was emitted.");
        if (_pasting) (pasteRejected ?? inputRejected)?.Invoke("Terminal input ended during bracketed paste; nothing was inserted.");
        else if (_state == State.Escape)
            EmitEscape(_altSequence);
        else if (_state != State.Ground) Reject("Terminal input ended with an incomplete control sequence; it was discarded.");
        DiscardUnixInput();
    }

    public void Dispose() { _inputBytes.Dispose(); _paste.Dispose(); }

    public void DiscardUnixInput()
    {
        _inputBytes.Clear();
        _length = _pasteMatch = _mouseRemaining = 0;
        _pasting = _pasteTooLarge = _pasteInvalidEncoding = _discarding = _sawEscape = _altSequence = _unixInput = false;
        _state = State.Ground;
        _paste.Clear();
    }

    private void Reject(string message)
    {
        LastRejectedInput = message;
        RejectedInputCount++;
        (inputRejected ?? pasteRejected)?.Invoke(message);
    }

    private void EmitConsole(ConsoleKeyInfo input)
    {
        LastKeyInput = TerminalKeyInput.FromConsole(input);
        if (richKey is not null) richKey(LastKeyInput);
        else key(input);
    }

    private void EmitRaw(Rune rune, bool alt = false, bool extraEscape = false)
    {
        LastKeyInput = TerminalKeyInput.FromRaw(rune, alt, extraEscape);
        if (richKey is not null) { richKey(LastKeyInput); return; }
        // Preserve the old UTF-16/C0 ConsoleKeyInfo delivery contract when no richer
        // consumer is installed. The rich route receives the intact scalar once.
        Span<char> characters = stackalloc char[2];
        var count = rune.EncodeToUtf16(characters);
        for (var index = 0; index < count; index++) key(CharacterKey(characters[index], alt ? ConsoleModifiers.Alt : ConsoleModifiers.None, rawControls: true));
    }

    private void EmitEscape(bool alt) => EmitRaw(new Rune('\x1b'), alt);

    private void EmitKey(TerminalKeyInput input)
    {
        LastKeyInput = input;
        if (richKey is not null) { richKey(input); return; }
        var projection = input.ProjectConsoleKeys();
        if (projection.SuppressedRelease) { SuppressedKeyReleaseCount++; return; }
        // Splitting a single Unicode scalar into UTF-16 units is the existing editor
        // compatibility path. Enhanced modifiers/layout/text/repeat metadata is not.
        if (projection.Loss is not (TerminalKeyProjectionLoss.None or TerminalKeyProjectionLoss.Utf16Split))
        {
            Reject($"Terminal key needs a rich input consumer; ConsoleKeyInfo would lose {projection.Loss}.");
            return;
        }
        foreach (var inputKey in projection.Keys) key(inputKey);
    }

    public void Feed(ConsoleKeyInfo input) => FeedCore(input, raw: false);

    private void FeedCore(ConsoleKeyInfo input, bool raw)
    {
        _lastInput = _clock.GetTimestampMilliseconds();
        var character = input.KeyChar;
        if (_pasting)
        {
            if (character == PasteEnd[_pasteMatch])
            {
                if (++_pasteMatch != PasteEnd.Length) return;
                var text = _pasteTooLarge || _pasteInvalidEncoding ? null : _paste.ToString();
                _paste.Clear();
                _pasteMatch = 0;
                _pasting = false;
                if (text is null) (pasteRejected ?? inputRejected)?.Invoke(_pasteInvalidEncoding
                    ? "Paste contains invalid UTF-8; nothing was inserted."
                    : $"Paste exceeds {TerminalTextEditing.MaximumPasteLength:N0} UTF-16 code units; nothing was inserted.");
                else paste(text);
                _pasteTooLarge = false;
                _pasteInvalidEncoding = false;
                return;
            }
            // The closing marker has no internal ESC, so only a new ESC can overlap.
            if (_pasteMatch > 0) AppendPaste(PasteEnd.AsSpan(0, _pasteMatch));
            _pasteMatch = character == '\x1b' ? 1 : 0;
            if (_pasteMatch == 0) AppendPaste(new ReadOnlySpan<char>(in character));
            return;
        }

        if (_state == State.Ground)
        {
            // Windows reports Ctrl+Backspace as DEL, sometimes without a useful key code.
            // Do not reinterpret POSIX DEL, where it commonly represents plain Backspace.
            if (!raw && OperatingSystem.IsWindows() && character == '\u007f')
                input = new ConsoleKeyInfo('\b', ConsoleKey.Backspace,
                    input.Modifiers.HasFlag(ConsoleModifiers.Shift), input.Modifiers.HasFlag(ConsoleModifiers.Alt), true);
            if (character != '\x1b' || input.Modifiers != ConsoleModifiers.None)
            {
                if (raw && Rune.TryCreate(character, out var rune)) EmitRaw(rune, input.Modifiers.HasFlag(ConsoleModifiers.Alt));
                else EmitConsole(input);
                return;
            }
            _sequence[0] = character;
            _length = 1;
            _state = State.Escape;
            _altSequence = false;
            return;
        }
        if (_state == State.Escape)
        {
            _state = character switch
            {
                '[' => State.Csi,
                'O' => State.Ss3,
                ']' or 'P' or '_' or '^' or 'X' => State.ControlString,
                _ => State.Ground
            };
            if (_state != State.Ground)
            {
                _osc = character == ']';
                Append(character);
                return;
            }
            _length = 0;
            if (character == '\x1b')
            {
                if (raw)
                {
                    // A second ESC is an Alt prefix for the following key/CSI sequence.
                    // Keep one introducer in the shared parser buffer.
                    if (_altSequence) EmitEscape(true);
                    _altSequence = !_altSequence;
                    _sequence[0] = character;
                    _length = 1;
                    _state = State.Escape;
                    return;
                }
                EmitEscape(false);
                _sequence[0] = character;
                _length = 1;
                _state = State.Escape;
                return;
            }
            if (!raw && character == '\0')
            {
                // A decoded Console key is not a character in an escape sequence.
                EmitEscape(false);
                EmitConsole(input);
                return;
            }
            if (raw)
            {
                EmitRaw(new Rune(character), true, _altSequence);
                _altSequence = false;
                return;
            }
            EmitConsole(new ConsoleKeyInfo(character, input.Key,
                input.Modifiers.HasFlag(ConsoleModifiers.Shift), true,
                input.Modifiers.HasFlag(ConsoleModifiers.Control)));
            return;
        }

        Append(character);
        if (_state == State.ControlString)
        {
            var terminated = (_osc && character == '\a') || (_sawEscape && character == '\\');
            _sawEscape = character == '\x1b';
            if (terminated) Complete();
            return;
        }
        if (_state == State.Mouse)
        {
            if (--_mouseRemaining == 0) Complete();
            return;
        }
        // The source rxvt shift-key forms end in '$', an otherwise intermediate CSI byte.
        if (!_discarding && _state == State.Csi && _length == 4 && character == '$' && _sequence[2] is '2' or '3' or '5' or '6' or '7' or '8')
        { Complete(); return; }
        if (character is < '@' or > '~') return;
        // Linux console function keys use CSI [ A through CSI [ E.
        if (!_discarding && _state == State.Csi && _length == 3 && character == '[') return;
        if (!_discarding && _state == State.Csi && _length == 3 && character == 'M')
        {
            // X10 carries three coordinate characters after the CSI final byte.
            _state = State.Mouse;
            _mouseRemaining = 3;
            return;
        }
        Complete();
    }

    private void AppendPaste(ReadOnlySpan<char> text)
    {
        if (_pasteTooLarge || _pasteInvalidEncoding) return;
        if ((long)_paste.Length + text.Length > TerminalTextEditing.MaximumPasteLength)
        {
            _paste.Clear();
            _pasteTooLarge = true;
            return;
        }
        _paste.Append(text);
    }

    private void Append(char character)
    {
        if (_discarding) return;
        if (_length < _sequence.Length) { _sequence[_length++] = character; return; }
        _discarding = true;
        _length = 0;
    }

    private void Complete()
    {
        var length = _length;
        var discarded = _discarding;
        var alt = _altSequence;
        _state = State.Ground;
        _length = 0;
        _discarding = false;
        _sawEscape = false;
        _altSequence = false;
        if (discarded) { Reject("An oversized or timed-out terminal control sequence was discarded."); return; }
        Dispatch(_sequence.AsSpan(0, length), alt);
    }

    private void Dispatch(ReadOnlySpan<char> sequence, bool alt = false)
    {
        if (sequence.SequenceEqual("\x1b[200~"))
        {
            _pasting = true;
            _paste.Clear();
            _pasteTooLarge = false;
            _pasteInvalidEncoding = false;
            _pasteMatch = 0;
            return;
        }
        if (sequence.SequenceEqual("\x1b[I") || sequence.SequenceEqual("\x1b[O"))
        {
            focus?.Invoke(sequence[^1] == 'I');
            return;
        }
        if (TryPointer(sequence, out var mouse))
        {
            pointer?.Invoke(mouse);
            return;
        }
        // CPR and modified F3 overlap. Match cursor reports only while the host expects a query reply.
        if (CapabilityResponse().IsMatch(sequence)
            || ((cursorReportExpected?.Invoke() ?? _clock.GetTimestampMilliseconds() < _startupDeadline)
                && CursorReport().IsMatch(sequence)))
        {
            response(sequence.ToString());
            return;
        }
        // Host-owned, solicited replies must be consumed before key parsing and
        // unknown-protocol rejection. Bracketed-paste content never reaches here.
        if (consumeResponse?.Invoke(sequence.ToString()) == true) return;
        if (TryEncodedCharacter(sequence, alt)) return;
        if (TryKey(sequence, out var decoded, out var modifiers, out var protocolCode, out var wireModifiers))
        {
            EmitKey(TerminalKeyInput.FromLegacy(decoded, (alt ? "\x1b" : "") + sequence.ToString(), protocolCode) with
            {
                Modifiers = modifiers | (alt ? TerminalKeyModifiers.Alt : TerminalKeyModifiers.None), AltPrefix = alt, WireModifiers = wireModifiers
            });
            return;
        }
        unknownResponse?.Invoke(sequence.ToString());
        if (_unixInput && (sequence.StartsWith("\x1b[") || sequence.StartsWith("\x1bO")))
            Reject("Unsupported terminal key/control protocol was not converted to a key event.");
    }

    private bool TryEncodedCharacter(ReadOnlySpan<char> sequence, bool alt)
    {
        if (TerminalKeyInputParser.TryParse(sequence, out var input, out var error))
        {
            EmitKey(alt ? input!.WithAltPrefix() : input!);
            return true;
        }
        if (error is null) return false;
        unknownResponse?.Invoke(sequence.ToString());
        Reject(error);
        return true;
    }

    private static ConsoleKeyInfo CharacterKey(char character, ConsoleModifiers flags, bool rawControls)
    {
        var code = character switch
        {
            '\r' => ConsoleKey.Enter, '\t' => ConsoleKey.Tab, '\b' or '\x7f' => ConsoleKey.Backspace,
            '\x1b' => ConsoleKey.Escape, ' ' or '\0' => ConsoleKey.Spacebar,
            // C0 aliases identify logical shortcuts, not physical keys. LF remains Ctrl+J,
            // distinct from CR/Return because ConsoleKeyInfo has no Linefeed key.
            >= '\x01' and <= '\x1a' => ConsoleKey.A + character - 1,
            >= 'a' and <= 'z' => ConsoleKey.A + character - 'a',
            >= 'A' and <= 'Z' => ConsoleKey.A + character - 'A',
            >= '0' and <= '9' => ConsoleKey.D0 + character - '0',
            _ => (ConsoleKey)0
        };
        if (rawControls && (character is '\0' or >= '\x01' and <= '\x1a' or >= '\x1c' and <= '\x1f')
            && character is not ('\r' or '\t' or '\b')) flags |= ConsoleModifiers.Control;
        var text = character switch { '\x7f' => '\b', '\0' => ' ', >= '\x1c' and <= '\x1f' => (char)(character + 64), _ => character };
        // Casing does not prove Shift, nor does text reveal a keyboard scan/base code.
        return new(text, code, flags.HasFlag(ConsoleModifiers.Shift), flags.HasFlag(ConsoleModifiers.Alt), flags.HasFlag(ConsoleModifiers.Control));
    }

    private static bool TryKey(ReadOnlySpan<char> sequence, out ConsoleKeyInfo decoded,
        out TerminalKeyModifiers modifiers, out string protocolCode, out int? wireModifiers)
    {
        decoded = default;
        modifiers = TerminalKeyModifiers.None;
        protocolCode = "";
        wireModifiers = null;
        if (sequence.Length < 3 || sequence[1] is not ('[' or 'O')) return false;
        if (sequence.Length == 4 && sequence[1] == '[' && sequence[2] == '[' && sequence[3] is >= 'A' and <= 'E')
        {
            decoded = new ConsoleKeyInfo('\0', (ConsoleKey)((int)ConsoleKey.F1 + sequence[3] - 'A'), false, false, false);
            protocolCode = sequence[1..].ToString();
            return true;
        }
        var parameters = sequence[(sequence.Length > 3 && sequence[2] == '[' ? 3 : 2)..^1];
        var separator = parameters.IndexOf(';');
        var first = 1;
        var modifier = 1;
        if (separator >= 0)
        {
            if (!int.TryParse(parameters[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out first)
                || !int.TryParse(parameters[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out modifier)) return false;
            wireModifiers = modifier;
        }
        else if (!parameters.IsEmpty)
        {
            if (!int.TryParse(parameters, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            if (sequence[^1] is '~' or '^' or '$') first = value;
            else { modifier = value; wireModifiers = value; }
        }
        if (modifier is < 1 or > 32) return false;
        if (sequence[^1] is '^' or '$' && first is not (2 or 3 or 5 or 6 or 7 or 8)) return false;
        var linuxPrefix = sequence.Length > 3 && sequence[1] == '[' && sequence[2] == '[';
        if (linuxPrefix && !(sequence[^1] is >= 'A' and <= 'E' || sequence[^1] == '~' && first is 5 or 6)) return false;
        var code = linuxPrefix && sequence[^1] is >= 'A' and <= 'E'
            ? (ConsoleKey?)((int)ConsoleKey.F1 + sequence[^1] - 'A')
            : sequence[^1] is '~' or '^' or '$' ? first switch
        {
            1 or 7 => ConsoleKey.Home,
            2 => ConsoleKey.Insert,
            3 => ConsoleKey.Delete,
            4 or 8 => ConsoleKey.End,
            5 => ConsoleKey.PageUp,
            6 => ConsoleKey.PageDown,
            11 => ConsoleKey.F1, 12 => ConsoleKey.F2, 13 => ConsoleKey.F3, 14 => ConsoleKey.F4, 15 => ConsoleKey.F5,
            17 => ConsoleKey.F6, 18 => ConsoleKey.F7, 19 => ConsoleKey.F8, 20 => ConsoleKey.F9, 21 => ConsoleKey.F10,
            23 => ConsoleKey.F11, 24 => ConsoleKey.F12, 29 => ConsoleKey.Applications, 57427 => ConsoleKey.Clear,
            _ => (ConsoleKey?)null
        } : first == 1 ? sequence[^1] switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            'Z' => ConsoleKey.Tab,
            'P' => ConsoleKey.F1,
            'Q' => ConsoleKey.F2,
            'R' => ConsoleKey.F3,
            'S' => ConsoleKey.F4,
            'E' => ConsoleKey.Clear,
            'a' => ConsoleKey.UpArrow, 'b' => ConsoleKey.DownArrow, 'c' => ConsoleKey.RightArrow,
            'd' => ConsoleKey.LeftArrow, 'e' => ConsoleKey.Clear,
            'M' when sequence[1] == 'O' => ConsoleKey.Enter,
            >= 'p' and <= 'y' when sequence[1] == 'O' => ConsoleKey.NumPad0 + sequence[^1] - 'p',
            'j' when sequence[1] == 'O' => ConsoleKey.Multiply,
            'k' when sequence[1] == 'O' => ConsoleKey.Add,
            'l' when sequence[1] == 'O' => ConsoleKey.Separator,
            'm' when sequence[1] == 'O' => ConsoleKey.Subtract,
            'n' when sequence[1] == 'O' => ConsoleKey.Decimal,
            'o' when sequence[1] == 'O' => ConsoleKey.Divide,
            'X' when sequence[1] == 'O' => ConsoleKey.OemPlus,
            _ => (ConsoleKey?)null
        } : null;
        if (code is null) return false;
        var flags = modifier - 1;
        var character = code.Value switch
        {
            ConsoleKey.Tab => '\t', ConsoleKey.Enter => '\r',
            >= ConsoleKey.NumPad0 and <= ConsoleKey.NumPad9 => (char)('0' + code.Value - ConsoleKey.NumPad0),
            ConsoleKey.Multiply => '*', ConsoleKey.Add => '+', ConsoleKey.Separator => ',', ConsoleKey.Subtract => '-',
            ConsoleKey.Decimal => '.', ConsoleKey.Divide => '/', ConsoleKey.OemPlus => '=', _ => '\0'
        };
        decoded = new ConsoleKeyInfo(character, code.Value,
            (flags & 1) != 0 || sequence[^1] is 'Z' or '$' || sequence[1] == '[' && sequence[^1] is >= 'a' and <= 'e',
            (flags & 2) != 0, (flags & 4) != 0 || sequence[^1] == '^' || sequence[1] == 'O' && sequence[^1] is >= 'a' and <= 'e');
        modifiers = (TerminalKeyModifiers)flags | TerminalKeyInput.FromConsoleModifiers(decoded.Modifiers);
        var prefix = linuxPrefix ? "[[" : sequence[1].ToString();
        protocolCode = prefix + (sequence[^1] is '~' or '^' or '$' ? first.ToString(CultureInfo.InvariantCulture) : "") + sequence[^1];
        return true;
    }

    public void FlushEscape()
    {
        if (_pasting || _state == State.Ground || _inputBytes.Length > 0) return;
        if (_clock.GetTimestampMilliseconds() - _lastInput < (_state == State.Escape ? 60 : 1000)) return;
        if (_state == State.Escape)
        {
            _state = State.Ground;
            _length = 0;
            EmitEscape(_altSequence);
            _altSequence = false;
            return;
        }
        // Do not release a timed-out control-string tail into application input.
        _discarding = true;
        _length = 0;
    }

    // Anchored equivalents of upstream terminal-capability-detection.ts; CPR is query-dependent above.
    [GeneratedRegex(@"\A(?:\x1b\[\?[0-9]+(?:;[0-9]+)*\$y|\x1bP>\|[\s\S]*?\x1b\\|\x1bP(?:1\+r4[dD]73(?:=[^\x1b]*)?|0\+r(?:4[dD]73)?)\x1b\\|\x1b_G[\s\S]*?\x1b\\|\x1b\[\?[0-9]+(?:;[0-9]+)?u|\x1b\[\?[0-9;]*c|\x1b\]99;[^\x07\x1b]*i=opentui-notifications[^\x07\x1b]*p=\?[\s\S]*?(?:\x07|\x1b\\)|\x1b\]1337;Capabilities=[\s\S]*?(?:\x07|\x1b\\))\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CapabilityResponse();

    [GeneratedRegex(@"\A\x1b\[[0-9]+;[0-9]+R\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CursorReport();
}
