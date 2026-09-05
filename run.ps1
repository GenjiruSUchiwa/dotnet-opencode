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

$artifacts = Join-Path ([IO.Path]::GetTempPath()) ('opencode-dotnet-cli-' + [Guid]::NewGuid().ToString('N'))
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
    $stamp = Join-Path $artifacts 'expected-build.id'
    # Native PTY downloads are an explicit build choice, never a runtime fallback.
    $assetProperties = @()
    if ($env:OPENCODE_DOTNET_PACKAGE_PTY -eq '1') {
        $assetProperties += '-property:OpenCodePackagePersistentPty=true'
        if ($env:OPENCODE_DOTNET_PTY_TARGET) { $assetProperties += "-property:OpenCodePtyRuntimeIdentifier=$env:OPENCODE_DOTNET_PTY_TARGET" }
        if ($env:OPENCODE_DOTNET_PTY_ARCHIVE) { $assetProperties += "-property:OpenCodePtyArchive=$([IO.Path]::GetFullPath($env:OPENCODE_DOTNET_PTY_ARCHIVE))" }
    }
    $identityArguments = @('msbuild', $protocol, '-target:GetApplicationBuildIdentity', '-nologo',
        '-property:Configuration=Debug', "-property:ArtifactsPath=$artifacts", '-property:ArtifactsPivots=run',
        '-property:UseAppHost=true', "-property:OpenCodeBuildStampFile=$stamp") + $assetProperties
    $child = Start-OwnedChild $dotnet $identityArguments $PSScriptRoot
    $child.WaitForExit()
    if ($child.ExitCode -ne 0) { throw 'Could not fingerprint the application build inputs.' }
    $child.Dispose()
    $child = $null
    $expectedBuild = ([IO.File]::ReadAllText($stamp)).Trim()
    if ($expectedBuild -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid application build fingerprint.' }
    $child = Start-OwnedChild $dotnet (@('build', $project, '--configuration', 'Debug', '--artifacts-path', $artifacts,
        '--disable-build-servers', '--nologo', '-p:ArtifactsPivots=run', '-p:UseAppHost=true', "-p:OpenCodeExpectedBuildId=$expectedBuild") + $assetProperties) $PSScriptRoot
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
        foreach ($output in @($cliOutput, $serverOutput)) {
            if (([IO.File]::ReadAllText((Join-Path $output 'opencode-build.id'))).Trim() -cne $expectedBuild) {
                throw 'CLI and Server were not produced from the same application fingerprint.'
            }
        }
        foreach ($name in @('OpenCode.Server.dll', 'OpenCode.Server.deps.json', 'OpenCode.Server.runtimeconfig.json')) {
            if (!(Test-Path -LiteralPath (Join-Path $serverOutput $name) -PathType Leaf)) {
                throw "The CLI build did not produce the complete Server runtime asset: $name"
            }
        }
        [IO.Directory]::CreateDirectory($packagedServer) | Out-Null
        foreach ($asset in Get-ChildItem -LiteralPath $serverOutput -Force) {
            Copy-Item -LiteralPath $asset.FullName -Destination $packagedServer -Recurse -Force
        }
        $executable = Join-Path $cliOutput $(if ($IsWindows) { 'OpenCode.Cli.exe' } else { 'OpenCode.Cli' })
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
    # Never touch normal bin/obj, live registration, or immutable daemon deployments.
    # Do not partially remove a live child's dependencies after interruption.
    try {
        if ($null -ne $child -and !$child.HasExited) {
            [Console]::Error.WriteLine("The child has not exited; its artifacts remain at '$artifacts'. No process was stopped.")
        }
        elseif (Test-Path -LiteralPath $artifacts) {
            Remove-Item -LiteralPath $artifacts -Recurse -Force -ErrorAction Stop
        }
    }
    catch {
        [Console]::Error.WriteLine("Could not remove this invocation's artifacts at '$artifacts'. No process was stopped. $($_.Exception.Message)")
    }
    finally {
        try { if ($null -ne $child) { $child.Dispose() } }
        catch { [Console]::Error.WriteLine("Could not release the child process handle: $($_.Exception.Message)") }
    }
}
exit $exitCode
