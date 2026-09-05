param([Parameter(Mandatory = $true)][string] $Source)
$ErrorActionPreference = 'Stop'
# Source extraction only; never starts OpenCode or a terminal host.
$text = [System.IO.File]::ReadAllText((Resolve-Path $Source))
$section = [regex]::Match($text, '(?s)export const Definitions = \{(.*?)\} satisfies Record<string, Definition>').Groups[1].Value
if (!$section) { throw 'Cannot locate TuiKeybind.Definitions' }
$entries = [regex]::Matches($section, '(?m)^\s*(?:"(?<id>[^"]+)"|(?<id>leader)): keybind\((?<value>LeaderDefault|"[^"]*"|\{ key: "[^"]+", preventDefault: false \}), "(?<description>[^"]*)"\),')
if ($entries.Count -ne ([regex]::Matches($section, ': keybind\(')).Count) { throw 'An unrecognized default shape needs an explicit port' }
$lines = foreach ($entry in $entries) {
    $value = $entry.Groups['value'].Value
    $prevent = 'true'
    if ($value -eq 'LeaderDefault') { $value = '"ctrl+x"' }
    if ($value.StartsWith('{')) {
        $value = [regex]::Match($value, 'key: ("[^"]+")').Groups[1].Value
        $prevent = 'false'
    }
    '        new("' + $entry.Groups['id'].Value + '", ' + $value + ', "' + $entry.Groups['description'].Value + '", ' + $prevent + '),'
}
$output = @(
    '// Generated from packages/tui/src/config/keybind.ts, TuiKeybind.Definitions.'
    '// Regenerate with GenerateDefaults.ps1 -Source <TypeScript keybind.ts>.'
    'namespace OpenCode.Cli.Tui.Keymap;'
    ''
    'public sealed record DefaultBinding(string Id, string Key, string Description, bool PreventDefault);'
    ''
    'public static class DefaultBindings'
    '{'
    '    public static IReadOnlyList<DefaultBinding> All { get; } = Array.AsReadOnly<DefaultBinding>(['
) + $lines + @('    ]);', '}')
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'DefaultBindings.g.cs'), ($output -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
