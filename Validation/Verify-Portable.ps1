[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) ('QN Agent 便携验证 ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verificationRoot | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $verificationRoot)
$package = @(Get-ChildItem -LiteralPath $verificationRoot -Directory)
if ($package.Count -ne 1) { throw 'Expected one package directory.' }
$packageRoot = $package[0].FullName
$executable = Join-Path $packageRoot 'QNAgent.exe'
if (Test-Path -LiteralPath (Join-Path $packageRoot 'Data')) { throw 'Distributed ZIP must not contain Data.' }

# Changes apply only to this verification process and its children.
$env:DOTNET_ROOT = Join-Path $verificationRoot 'nonexistent-dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = [Environment]::GetFolderPath('System') + ';' + $env:WINDIR
foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
    if ($key -match '(?i)(TYPESAFE|OPENAI|OPENROUTER|ANTHROPIC|GEMINI|API.?KEY)') {
        [Environment]::SetEnvironmentVariable($key, $null, 'Process')
    }
}
$smokeOut = Join-Path $verificationRoot 'smoke-stdout.txt'
$smokeError = Join-Path $verificationRoot 'smoke-stderr.txt'
$smoke = Start-Process -FilePath $executable -ArgumentList '--portable-checks' -WorkingDirectory $env:WINDIR -WindowStyle Hidden -PassThru -RedirectStandardOutput $smokeOut -RedirectStandardError $smokeError
if (!$smoke.WaitForExit(55000)) { throw "Smoke verification still running: PID $($smoke.Id), output $smokeOut" }
$smoke.Refresh()
Get-Content -LiteralPath $smokeOut
if ($smoke.ExitCode -ne 0) { Get-Content -LiteralPath $smokeError; throw "Portable smoke failed: $($smoke.ExitCode)" }

$normalOut = Join-Path $verificationRoot 'normal-stdout.txt'
$normalError = Join-Path $verificationRoot 'normal-stderr.txt'
$normal = Start-Process -FilePath $executable -WorkingDirectory $env:WINDIR -WindowStyle Hidden -PassThru -RedirectStandardOutput $normalOut -RedirectStandardError $normalError
try {
    $statusPath = Join-Path $packageRoot 'Data\app-status.json'
    for ($n=0; $n -lt 60; $n++) {
        $normal.Refresh()
        if ($normal.HasExited) { throw 'Normal portable launch exited before opening.' }
        if (Test-Path -LiteralPath $statusPath) { break }
        Start-Sleep -Milliseconds 200
    }
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($status.State -ne 'WindowOpen' -or $status.ProcessId -ne $normal.Id) { throw 'Normal startup state mismatch.' }
    $business = Get-Content -LiteralPath (Join-Path $packageRoot 'Data\business.json') -Raw | ConvertFrom-Json
    foreach ($field in @('Shops','Products','Policies','Advice','Candidates','Rules')) {
        if (@($business.$field).Count -ne 0) { throw 'Normal profile is not empty.' }
    }
    if (Test-Path -LiteralPath (Join-Path $packageRoot 'Data\settings.json')) { throw 'Unexpected preconfigured settings in fresh profile.' }
    if (Test-Path -LiteralPath (Join-Path $packageRoot 'Data\inbox.json')) { throw 'Unexpected chat data in fresh profile.' }
    Write-Output 'PASS: normal startup from another working directory creates an empty EXE-local profile'
    $normal.Refresh()
    if (!$normal.CloseMainWindow()) { throw 'Could not gracefully close the verification window.' }
    if (!$normal.WaitForExit(15000)) { throw 'Verification window did not close.' }
} finally {
    # This is only the exact test process created above, never a user's running assistant.
    $normal.Refresh()
    if (!$normal.HasExited) { $normal.Kill($true); $normal.WaitForExit(5000) | Out-Null }
}
$diagnostics = Join-Path $verificationRoot 'clean-diagnostics.zip'
$exportOut = Join-Path $verificationRoot 'export-stdout.txt'
$exportError = Join-Path $verificationRoot 'export-stderr.txt'
$export = Start-Process -FilePath $executable -ArgumentList @('--export-diagnostics',('"' + $diagnostics + '"')) -WorkingDirectory $env:WINDIR -WindowStyle Hidden -PassThru -RedirectStandardOutput $exportOut -RedirectStandardError $exportError
if (!$export.WaitForExit(15000)) { throw 'Diagnostic export timed out.' }
$export.Refresh()
if ($export.ExitCode -ne 0 -or !(Test-Path -LiteralPath $diagnostics)) { Get-Content -LiteralPath $exportError; throw 'Portable diagnostic export failed.' }
Write-Output 'PASS: command-line diagnostics exports only from portable Data'
Write-Output "VERIFICATION_ROOT=$verificationRoot"
Write-Output 'The original ZIP and clean staging directory were not modified by these tests.'
