#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'This publishing scanner bootstrap requires Windows x64.'
}

$version = '8.30.1'
$expected = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e'
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$directory = Join-Path $temporaryRoot ('dotnet-opencode-gitleaks-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$archive = Join-Path $directory 'gitleaks.zip'
Invoke-WebRequest -Uri "https://github.com/gitleaks/gitleaks/releases/download/v$version/gitleaks_${version}_windows_x64.zip" -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expected) {
    throw 'Gitleaks archive does not match the reviewed SHA-256.'
}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $directory 'tool')
$report = Join-Path $directory 'gitleaks-report.json'
& (Join-Path $directory 'tool/gitleaks.exe') git . --log-opts=--all --config .gitleaks.toml --redact --no-banner --report-format json --report-path $report
if ($LASTEXITCODE -ne 0) {
    throw "Secret scan failed. Publication is blocked; the redacted report is at '$report'."
}
