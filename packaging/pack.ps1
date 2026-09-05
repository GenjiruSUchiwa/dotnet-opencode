#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $ArtifactsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# The filename contract uses NuGet-normalized SemVer without build metadata.
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$') {
    throw 'Version must be SemVer major.minor.patch[-prerelease], without build metadata.'
}
if ($Version.Contains('-')) {
    foreach ($identifier in $Version.Substring($Version.IndexOf('-') + 1).Split('.')) {
        if ($identifier -cmatch '^0[0-9]+$') { throw 'Numeric prerelease identifiers must not have leading zeros.' }
    }
}
if (![IO.Path]::IsPathFullyQualified($ArtifactsPath)) { throw 'ArtifactsPath must be absolute.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath($ArtifactsPath)
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if ($artifacts.Equals($root, $comparison) -or $artifacts.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $comparison)) {
    throw 'ArtifactsPath must be outside the source checkout. No source or user artifacts are deleted.'
}
$pin = (Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json).sdk.version
$sdk = if ($env:OPENCODE_DOTNET_SDK_ROOT) { [IO.Path]::GetFullPath($env:OPENCODE_DOTNET_SDK_ROOT) } else { Join-Path $root '.dotnet' }
$dotnet = Join-Path $sdk $(if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' })
if (!(Test-Path -LiteralPath $dotnet -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $sdk "sdk/$pin") -PathType Container)) {
    throw "Install pinned SDK $pin in '$sdk' or explicitly select its SDK root with OPENCODE_DOTNET_SDK_ROOT. PATH fallback is disabled."
}
$packages = Join-Path $artifacts 'packages'
$package = Join-Path $packages "dotnet-opencode.$Version.nupkg"
if (Test-Path -LiteralPath $package) { throw "Package already exists: $package. Use a new version or artifact directory; no package was overwritten." }
# Fresh build/publish staging prevents stale outputs from entering a release.
$build = Join-Path $artifacts ('build/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($build) | Out-Null
[IO.Directory]::CreateDirectory($packages) | Out-Null
$environment = @{
    DOTNET_ROOT = $sdk
    DOTNET_HOST_PATH = $dotnet
    DOTNET_CLI_HOME = (Join-Path $build '.cli-home')
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
}
$previous = @{}
foreach ($name in $environment.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
}
Push-Location $root
try {
    & $dotnet pack (Join-Path $root 'src/OpenCode.Cli/OpenCode.Cli.csproj') --configuration Release `
        --artifacts-path $build --output $packages "-p:PackageVersion=$Version" `
        -p:OpenApiGenerateDocuments=false -p:NuGetAudit=true --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Pinned CLI pack failed (exit $LASTEXITCODE). Build evidence remains at '$build'." }
    if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw "Pack did not produce the required artifact: $package" }
    & (Join-Path $PSScriptRoot 'inspect.ps1') -PackagePath $package -Version $Version
    if (!$?) { throw 'Static tool package inspection failed.' }
    Write-Output $package
}
finally {
    Pop-Location
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
# Deliberately no push, install, execution, signing-key lookup, or artifact cleanup.
