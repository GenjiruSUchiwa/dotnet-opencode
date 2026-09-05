using System.Text.RegularExpressions;

namespace OpenTui.Blazor.Keymap;

/// <summary>The installed @opentui/keymap 0.5.9 default parser and optional comma expander.</summary>
public sealed class KeySequenceParser
{
    private static readonly string[] Names = ("up down left right clear escape return linefeed enter tab backspace delete insert home end pageup pagedown space " +
        "lt gt plus minus equal comma period slash backslash semicolon quote backquote leftbracket rightbracket capslock numlock scrolllock printscreen pause menu apps " +
        "kp0 kp1 kp2 kp3 kp4 kp5 kp6 kp7 kp8 kp9 kpdecimal kpdivide kpmultiply kpminus kpplus kpenter kpequal kpseparator kpleft kpright kpup kpdown kppageup kppagedown kphome kpend kpinsert kpdelete " +
        "mediaplay mediapause mediaplaypause mediareverse mediastop mediafastforward mediarewind medianext mediaprev mediarecord volumedown volumeup mute " +
        "leftshift leftctrl leftalt leftsuper lefthyper leftmeta rightshift rightctrl rightalt rightsuper righthyper rightmeta iso_level3_shift iso_level5_shift option alt meta super hyper control ctrl shift")
        .Split(' ').OrderByDescending(name => name.Length).ToArray();
    private static readonly string[] Modifiers = ["control", "option", "shift", "super", "hyper", "ctrl", "meta", "alt"];
    private readonly IReadOnlyDictionary<string, KeyStroke> _tokens;
    private readonly Func<string, string> _expand;
    private readonly bool _commaBindings;

    public KeySequenceParser(IReadOnlyDictionary<string, KeyStroke>? tokens = null,
        bool commaBindings = false, Func<string, string>? expand = null)
    {
        _tokens = tokens ?? new Dictionary<string, KeyStroke>();
        _commaBindings = commaBindings;
        _expand = expand ?? (value => value);
    }

    public IReadOnlyList<IReadOnlyList<KeySequencePart>> Parse(BindingKey key)
    {
        if (key is BindingKey.Stroke stroke) return [new[] { new KeySequencePart(stroke.Value) }];
        var text = ((BindingKey.Text)key).Value;
        var alternatives = _commaBindings && text.Contains(',') ? text.Split(',').Select(part => part.Trim()).ToArray() : [text];
        if (alternatives.Any(value => value.Length == 0)) throw new FormatException("Key bindings cannot contain empty entries.");
        return alternatives.Select(value => ParseSequence(_expand(value))).ToArray();
    }

    private IReadOnlyList<KeySequencePart> ParseSequence(string input)
    {
        // The default parser treats whitespace plus modifiers as one chord, not Emacs syntax.
        if (input.Contains('+') && input.Any(char.IsWhiteSpace))
        {
            var parts = input.Split('+').Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
            var names = parts.Where(part => !Modifiers.Contains(part.ToLowerInvariant())).ToArray();
            if (names.Length != 1) throw new FormatException($"Invalid key '{input}': expected one key name.");
            return [new KeySequencePart(CreateStroke(names[0], parts.Select(part => part.ToLowerInvariant()).ToArray()))];
        }
        var result = new List<KeySequencePart>();
        for (var index = 0; index < input.Length;)
        {
            if (input[index] == '<' && input.IndexOf('>', index) is var end && end >= 0)
            {
                var token = input[(index + 1)..end].Trim().ToLowerInvariant();
                if (!_tokens.TryGetValue(token, out var value)) throw new FormatException($"Unknown key token '<{token}>'.");
                result.Add(new(value, token));
                index = end + 1;
                continue;
            }
            if (input[index] == '{' && input.IndexOf('}', index) >= 0)
                throw new FormatException("Sequence patterns require a pattern resolver; none is registered.");
            var modifiers = new List<string>();
            while (Modifiers.FirstOrDefault(modifier => input.AsSpan(index).StartsWith(modifier + "+", StringComparison.OrdinalIgnoreCase)) is { } modifier)
            {
                modifiers.Add(modifier);
                index += modifier.Length + 1;
            }
            if (index == input.Length) throw new FormatException($"Invalid key '{input}': missing key name.");
            var name = Names.FirstOrDefault(name => input.AsSpan(index).StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (name is null && input[index] is 'f' or 'F' && index + 1 < input.Length && char.IsAsciiDigit(input[index + 1]))
                name = input.Substring(index, index + 2 < input.Length && char.IsAsciiDigit(input[index + 2]) ? 3 : 2);
            name ??= input[index].ToString();
            result.Add(new(CreateStroke(name == " " ? "space" : name, modifiers)));
            index += name.Length;
        }
        if (result.Count == 0) throw new FormatException("Key sequences cannot be empty.");
        return result.AsReadOnly();
    }

    private static KeyStroke CreateStroke(string name, IReadOnlyCollection<string> modifiers) => new(name,
        modifiers.Contains("ctrl") || modifiers.Contains("control"), modifiers.Contains("shift"),
        modifiers.Contains("meta") || modifiers.Contains("alt") || modifiers.Contains("option"),
        modifiers.Contains("super"), modifiers.Contains("hyper"));
}
