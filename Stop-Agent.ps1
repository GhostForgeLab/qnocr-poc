param([Parameter(Mandatory)][string]$LaunchDirectory)
$ErrorActionPreference = 'Stop'
$launch = Get-Content -LiteralPath (Join-Path $LaunchDirectory 'launch.json') -Raw | ConvertFrom-Json
if ($launch.CapturePid -and (Test-Path -LiteralPath $launch.RunDirectory)) {
    Set-Content -LiteralPath (Join-Path $launch.RunDirectory 'stop.request') -Value 'Operator stopped capture.'
}
$process = Get-Process -Id $launch.ConsolePid -ErrorAction SilentlyContinue
if ($process) {
    if ($process.StartTime.ToUniversalTime().Ticks -ne ([datetime]$launch.ConsoleStarted).ToUniversalTime().Ticks) { throw 'PID has been reused; refusing to stop an unrelated process.' }
    Stop-Process -InputObject $process
}
Write-Output 'Console stopped; capture will stop after its current operation.'
