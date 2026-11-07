[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $PSScriptRoot '.tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/cli-home'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (!$NoBuild) {
    & $dotnet restore DesktopClient/DesktopClient.csproj --source .tools/nuget --ignore-failed-sources -p:NuGetAudit=false -v q
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & $dotnet build DesktopClient/DesktopClient.csproj --no-restore -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
}
$executable = Join-Path $PSScriptRoot 'DesktopClient/bin/Release/net8.0-windows/win-x64/QNAgent.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'Build the desktop client first.' }
Start-Process -FilePath $executable -WorkingDirectory $PSScriptRoot | Out-Null
Write-Output "Opened: $executable"
