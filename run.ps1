#Requires -Version 7.0

# Usage: ./run.ps1 [--] [CLI arguments]. The caller's project directory is unchanged.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$cliArguments = @($args)
if ($cliArguments.Count -gt 0 -and $cliArguments[0] -eq '--') {
    $cliArguments = if ($cliArguments.Count -eq 1) { @() } else { @($cliArguments[1..($cliArguments.Count - 1)]) }
}

function Start-OwnedChild([string] $Executable, [string[]] $Arguments, [string] $WorkingDirectory) {
    $location = Get-Location
    if ($location.Provider.Name -ne 'FileSystem') { throw 'Run the CLI from a filesystem project directory.' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.UseShellExecute = $false
    $start.WorkingDirectory = if ($WorkingDirectory) { $WorkingDirectory } else { $location.ProviderPath }
    # Child-only environment: apphosts and any bare dotnet commands use this SDK/runtime.
    $start.Environment['DOTNET_ROOT'] = $sdkRoot
    $architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToUpperInvariant()
    $start.Environment["DOTNET_ROOT_$architecture"] = $sdkRoot
    $start.Environment['DOTNET_HOST_PATH'] = $dotnet
    $start.Environment['OPENCODE_DOTNET_HOST'] = $dotnet
    $start.Environment['DOTNET_CLI_HOME'] = Join-Path $sdkRoot '.cli-home'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['PATH'] = $sdkRoot + [IO.Path]::PathSeparator + $start.Environment['PATH']
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "Failed to start $Executable" }
    return $process
}

$artifacts = $null
$runtimeArtifacts = $null
$buildLock = $null
$exitCode = 1
$child = $null
try {
    $requiredSdk = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $sdkRoot = if ($env:OPENCODE_DOTNET_SDK_ROOT) { [IO.Path]::GetFullPath($env:OPENCODE_DOTNET_SDK_ROOT) } else { Join-Path $PSScriptRoot '.dotnet' }
    $dotnet = Join-Path $sdkRoot $(if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' })
    if (!(Test-Path -LiteralPath $dotnet -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $sdkRoot "sdk/$requiredSdk") -PathType Container)) {
        throw "Pinned SDK $requiredSdk is missing from '$sdkRoot'. Install it with the official dotnet-install script; see RUNNING.md. System SDK fallback is disabled."
    }
    $project = Join-Path $PSScriptRoot 'src/OpenCode.Cli/OpenCode.Cli.csproj'
    $protocol = Join-Path $PSScriptRoot 'src/OpenCode.Protocol/OpenCode.Protocol.csproj'
    # Native PTY downloads are an explicit build choice, never a runtime fallback.
    $assetProperties = @()
    if ($env:OPENCODE_DOTNET_PACKAGE_PTY -eq '1') {
        $assetProperties += '-property:OpenCodePackagePersistentPty=true'
        if ($env:OPENCODE_DOTNET_PTY_TARGET) { $assetProperties += "-property:OpenCodePtyRuntimeIdentifier=$env:OPENCODE_DOTNET_PTY_TARGET" }
        if ($env:OPENCODE_DOTNET_PTY_ARCHIVE) { $assetProperties += "-property:OpenCodePtyArchive=$([IO.Path]::GetFullPath($env:OPENCODE_DOTNET_PTY_ARCHIVE))" }
    }
    # Reuse compiler/restore outputs per checkout, SDK, architecture and PTY configuration.
    # Running clients never load assemblies from this mutable build directory.
    $flavor = $requiredSdk + '|' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture + '|' + ($assetProperties -join '|')
    $cacheKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($flavor))).ToLowerInvariant()
    $artifacts = Join-Path $PSScriptRoot "artifacts/run/$cacheKey"
    [IO.Directory]::CreateDirectory($artifacts) | Out-Null
    # Serialize builds and snapshot copying, not the lifetime of a running TUI.
    for ($attempt = 0; ; $attempt++) {
        try {
            $buildLock = [IO.File]::Open((Join-Path $artifacts 'build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            break
        }
        catch [IO.IOException] {
            if ($attempt -ge 600) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
    $stamp = Join-Path $artifacts 'expected-build.id'
    $identityArguments = @('msbuild', $protocol, '-target:GetApplicationBuildIdentity', '-nologo',
        '-property:Configuration=Debug', "-property:ArtifactsPath=$artifacts", '-property:ArtifactsPivots=run',
        '-property:UseAppHost=true', '-property:OpenApiGenerateDocuments=false', "-property:OpenCodeBuildStampFile=$stamp") + $assetProperties
    $child = Start-OwnedChild $dotnet $identityArguments $PSScriptRoot
    $child.WaitForExit()
    if ($child.ExitCode -ne 0) { throw 'Could not fingerprint the application build inputs.' }
    $child.Dispose()
    $child = $null
    $expectedBuild = ([IO.File]::ReadAllText($stamp)).Trim()
    if ($expectedBuild -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid application build fingerprint.' }
    $child = Start-OwnedChild $dotnet (@('build', $project, '--configuration', 'Debug', '--artifacts-path', $artifacts,
        '--nologo', '-p:ArtifactsPivots=run', '-p:UseAppHost=true', '-p:OpenApiGenerateDocuments=false', "-p:OpenCodeExpectedBuildId=$expectedBuild") + $assetProperties) $PSScriptRoot
    $child.WaitForExit()
    $exitCode = $child.ExitCode
    if ($exitCode -eq 0) {
        $child.Dispose()
        $child = $null
        $child = Start-OwnedChild $dotnet ($identityArguments + "-property:OpenCodeExpectedBuildId=$expectedBuild") $PSScriptRoot
        $child.WaitForExit()
        if ($child.ExitCode -ne 0) { throw 'Application sources changed during the build; no executable was launched.' }
        # Fixed MSBuild artifact pivots, not a recursive search that might select a dependency executable.
        $cliOutput = Join-Path $artifacts 'bin/OpenCode.Cli/run'
        $serverOutput = Join-Path $artifacts 'bin/OpenCode.Server/run'
        $packagedServer = Join-Path $cliOutput 'server'
        foreach ($output in @($cliOutput, $serverOutput, $packagedServer)) {
            if (([IO.File]::ReadAllText((Join-Path $output 'opencode-build.id'))).Trim() -cne $expectedBuild) {
                throw 'CLI and Server were not produced from the same application fingerprint.'
            }
        }
        foreach ($name in @('OpenCode.Server.dll', 'OpenCode.Server.deps.json', 'OpenCode.Server.runtimeconfig.json')) {
            if (!(Test-Path -LiteralPath (Join-Path $packagedServer $name) -PathType Leaf)) {
                throw "The CLI build did not produce the complete Server runtime asset: $name"
            }
        }
        # MSBuild already stages server/. Copy only the runnable payload, not bin/obj
        # caches, so another invocation can build while this client remains open.
        $temporaryRoot = if ($IsWindows -and (Test-Path -LiteralPath 'C:\tmp\opencode' -PathType Container)) { 'C:\tmp\opencode' } else { [IO.Path]::GetTempPath() }
        $runtimeArtifacts = Join-Path $temporaryRoot ('opencode-dotnet-cli-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($runtimeArtifacts) | Out-Null
        foreach ($asset in Get-ChildItem -LiteralPath $cliOutput -Force) {
            Copy-Item -LiteralPath $asset.FullName -Destination $runtimeArtifacts -Recurse -Force
        }
        $buildLock.Dispose()
        $buildLock = $null
        $executable = Join-Path $runtimeArtifacts $(if ($IsWindows) { 'OpenCode.Cli.exe' } else { 'OpenCode.Cli' })
        if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw "CLI apphost was not produced: $executable" }
        $child.Dispose()
        $child = $null
        $exitCode = 130
        $child = Start-OwnedChild $executable $cliArguments
        $child.WaitForExit()
        $exitCode = $child.ExitCode
    }
}
catch [System.Management.Automation.PipelineStoppedException] {
    $exitCode = 130
}
catch {
    $exitCode = 1
    [Console]::Error.WriteLine("Unable to build or launch the CLI: $($_.Exception.Message)")
}
finally {
    # Keep the incremental cache. Remove only this invocation's private runtime copy.
    # Never touch live registration or the daemon's immutable deployment.
    try {
        if ($null -ne $buildLock -and $null -ne $child -and !$child.HasExited) {
            # Do not release the shared-cache lock while our build child is still writing.
            $child.WaitForExit()
        }
        if ($null -ne $child -and !$child.HasExited) {
            [Console]::Error.WriteLine("The child has not exited; its runtime copy remains at '$runtimeArtifacts'. No process was stopped.")
        }
        elseif ($null -ne $runtimeArtifacts -and (Test-Path -LiteralPath $runtimeArtifacts)) {
            Remove-Item -LiteralPath $runtimeArtifacts -Recurse -Force -ErrorAction Stop
        }
    }
    catch {
        [Console]::Error.WriteLine("Could not remove this invocation's runtime copy at '$runtimeArtifacts'. No process was stopped. $($_.Exception.Message)")
    }
    finally {
        if ($null -ne $buildLock) { $buildLock.Dispose() }
        try { if ($null -ne $child) { $child.Dispose() } }
        catch { [Console]::Error.WriteLine("Could not release the child process handle: $($_.Exception.Message)") }
    }
}
exit $exitCode
