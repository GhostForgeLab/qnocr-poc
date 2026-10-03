param(
    [Parameter(Mandatory)][string]$BuyersPath,
    [string]$Prefix = ('1340' + [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('MMddHHmmss'))
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buyersFile = (Resolve-Path -LiteralPath $BuyersPath).Path
$buyers = Get-Content -LiteralPath $buyersFile -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($buyers.BuyerA) -or [string]::IsNullOrWhiteSpace($buyers.BuyerB)) {
    throw 'Configure both operator-confirmed test buyer labels in the local JSON file.'
}
if ($Prefix -notmatch '^[0-9]{6,}$') { throw 'Use a new numeric test marker of at least six digits.' }
$checks = Join-Path $projectRoot 'Validation\ChangeDetectionChecks\bin\Debug\net8.0-windows\win-x64\ChangeDetectionChecks.exe'
if (!(Test-Path -LiteralPath $checks)) { throw 'Build the main project and ChangeDetectionChecks first.' }
$root = Join-Path $projectRoot 'Output\ValidationV1340C'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$runName = 'live-' + [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $root $runName
Push-Location $projectRoot
try {
    Write-Output "Evidence: $output"
    Write-Output 'Follow STAGE_READY: send only the requested messages from that test buyer and switch the reception chat.'
    & $checks --real-multi-buyer-capture $buyersFile $output $Prefix 2>&1 | Tee-Object -FilePath ($output + '.capture.log')
    $captureExit = $LASTEXITCODE
    if ($captureExit -ne 0) { throw "Real acceptance did not pass (exit $captureExit). Preserve $output for diagnosis." }
    & $checks --real-multi-buyer-evidence $output 2>&1 | Tee-Object -FilePath ($output + '.evidence.log')
    if ($LASTEXITCODE -ne 0) { throw 'Independent evidence audit did not pass.' }
    Write-Output "REAL_ACCEPTANCE_PASSED: $output"
}
finally { Pop-Location }
