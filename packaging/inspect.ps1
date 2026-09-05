#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $PackagePath, [Parameter(Mandatory)][string] $Version)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
try {
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.StartsWith('/') -or $entry.FullName.Contains('\') -or $entry.FullName.Split('/') -contains '..') { throw "Unsafe archive path: $($entry.FullName)" }
        if (!$entries.TryAdd($entry.FullName, $entry)) { throw "Duplicate archive entry: $($entry.FullName)" }
    }
    function Require-Entry([string] $Name) {
        if (!$entries.ContainsKey($Name)) { throw "Missing package entry: $Name" }
        return $entries[$Name]
    }
    function Read-Entry([string] $Name) {
        $stream = (Require-Entry $Name).Open()
        $reader = [IO.StreamReader]::new($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    function Assert-Hash([string] $Name, [string] $Expected) {
        $stream = (Require-Entry $Name).Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($actual -cne $Expected) { throw "Package hash mismatch: $Name" }
    }
    $prefix = 'tools/net11.0/any/'
    $settings = [xml]::new()
    $settings.XmlResolver = $null
    $settings.LoadXml((Read-Entry ($prefix + 'DotnetToolSettings.xml')))
    $commands = @($settings.DotNetCliTool.Commands.Command)
    if ($commands.Count -ne 1 -or $commands[0].GetAttribute('Name') -cne 'dotnet-opencode' -or $commands[0].GetAttribute('Runner') -cne 'dotnet' -or $commands[0].GetAttribute('EntryPoint') -cne 'OpenCode.Cli.dll') {
        throw 'ToolSettings command, runner or entrypoint differs from the approved tool contract.'
    }
    $nuspecName = @($entries.Keys | Where-Object { $_.EndsWith('.nuspec') -and !($_.Contains('/')) })
    if ($nuspecName.Count -ne 1) { throw 'Expected one root nuspec.' }
    $nuspec = [xml]::new(); $nuspec.XmlResolver = $null; $nuspec.LoadXml((Read-Entry $nuspecName[0]))
    if ($nuspec.package.metadata.id -cne 'dotnet-opencode' -or $nuspec.package.metadata.version -cne $Version) { throw 'Package identity/version mismatch.' }
    if ($nuspec.package.metadata.packageTypes.packageType.name -cne 'DotnetTool') { throw 'Package is not a DotnetTool.' }
    Require-Entry 'README.md' | Out-Null
    foreach ($directory in @('', 'server/')) {
        $assembly = if ($directory) { 'OpenCode.Server' } else { 'OpenCode.Cli' }
        foreach ($extension in @('.dll', '.deps.json', '.runtimeconfig.json')) { Require-Entry ($prefix + $directory + $assembly + $extension) | Out-Null }
        $runtime = Read-Entry ($prefix + $directory + $assembly + '.runtimeconfig.json') | ConvertFrom-Json -AsHashtable
        $frameworks = @($runtime.runtimeOptions.frameworks)
        if (!($frameworks | Where-Object { $_.name -ceq 'Microsoft.NETCore.App' -and $_.version -ceq '11.0.0-preview.7.26381.103' })) { throw "Missing pinned .NET 11 runtime requirement: $assembly" }
        if (!($frameworks | Where-Object { $_.name -ceq 'Microsoft.AspNetCore.App' -and $_.version -ceq '11.0.0-preview.7.26381.103' })) { throw "Missing pinned ASP.NET Core 11 requirement: $assembly" }
        if (!$directory -and $runtime.runtimeOptions.rollForward -cne 'Disable') { throw 'CLI runtime roll-forward must remain disabled.' }
        $deps = Read-Entry ($prefix + $directory + $assembly + '.deps.json') | ConvertFrom-Json -AsHashtable
        foreach ($target in $deps.targets.Values) {
            foreach ($library in $target.Values) {
                foreach ($kind in @('runtime', 'native', 'resources', 'runtimeTargets')) {
                    if (!$library.ContainsKey($kind)) { continue }
                    foreach ($asset in $library[$kind].Keys) {
                        $leaf = $asset.Split('/')[-1]
                        if ($leaf -eq '_._') { continue }
                        $relative = if ($kind -eq 'runtimeTargets') { $asset }
                            elseif ($kind -eq 'resources' -and $library[$kind][$asset].ContainsKey('locale')) { $library[$kind][$asset].locale + '/' + $leaf }
                            else { $leaf }
                        Require-Entry ($prefix + $directory + $relative) | Out-Null
                    }
                }
            }
        }
    }
    $stamp = (Read-Entry ($prefix + 'opencode-build.id')).Trim()
    if ($stamp -cnotmatch '^[0-9a-f]{64}$' -or $stamp -cne (Read-Entry ($prefix + 'server/opencode-build.id')).Trim()) { throw 'CLI/Server source identities do not match.' }
    $inventory = Read-Entry ($prefix + 'tool-runtime-assets.json') | ConvertFrom-Json -AsHashtable
    foreach ($file in $inventory.files) {
        $entry = Require-Entry ($prefix + $file.path)
        if ($entry.Length -ne $file.bytes) { throw "Asset length mismatch: $($file.path)" }
        Assert-Hash ($prefix + $file.path) $file.sha256
    }
    Assert-Hash ($prefix + 'runtimes/win-x64/native/opentui.dll') '294550aa81a50439e42ecb73df97b99d6ec3dc2ecb1f3f18bb453076140e1f0e'
    Require-Entry ($prefix + 'THIRD-PARTY-NOTICES.md') | Out-Null
    Require-Entry ($prefix + 'third-party/opentui-0.5.9/LICENSE-GHOSTTY') | Out-Null
    Require-Entry ($prefix + 'Tui/Transcript/GrammarAssets/manifest.json') | Out-Null
    Write-Output ("Static package verified: {0}; {1} payload hashes; {2} grammar assets; {3} dependency records; entrypoint {4}; build {5}" -f `
        $Version, $inventory.files.Count, $inventory.grammarAssetCount, $inventory.dependencies.Count, $commands[0].GetAttribute('EntryPoint'), $stamp)
}
finally { $archive.Dispose() }
