param(
    [Parameter(Mandatory)][string]$QnbotRoot,
    [string]$EnvelopePath = (Join-Path $PSScriptRoot 'message_envelope.json'),
    [string]$IdentityMapping = (Join-Path $PSScriptRoot 'identity_mapping.json')
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
& (Join-Path $PSScriptRoot 'Prepare-QnbotSource.ps1') -QnbotRoot $QnbotRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$output = Join-Path $projectRoot ('Output\Integration\run_' + (Get-Date -Format yyyyMMdd_HHmmss_fff))
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build (Join-Path $PSScriptRoot 'QnbotCompatibility.csproj') -t:Rebuild --ignore-failed-sources 2>&1 |
    Tee-Object -FilePath (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Compatibility host build failed.' }
& (Join-Path $PSScriptRoot 'bin\Debug\net48\QnbotCompatibility.exe') $EnvelopePath $IdentityMapping $output 2>&1 |
    Tee-Object -FilePath (Join-Path $output 'run.log')
if ($LASTEXITCODE -ne 0) { throw 'Compatibility verification failed; inspect the local run log.' }
Write-Output "Offline compatibility result: $output"
