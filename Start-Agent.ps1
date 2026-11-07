[CmdletBinding()]
param(
    [string]$RunDirectory,
    [string]$StateDirectory,
    [string]$CaptureConfig,
    [ValidateRange(1024,65535)][int]$Port = 18730,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $PSScriptRoot '.tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/cli-home'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { throw 'Bundled .NET SDK missing: .tools/dotnet/dotnet.exe' }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } finally { $listener.Stop() }
if (!$NoBuild) {
    & $dotnet restore AgentConsole/AgentConsole.csproj --source .tools/nuget --ignore-failed-sources -p:NuGetAudit=false -v q
    if ($LASTEXITCODE -ne 0) { throw 'Agent Console dependency restore failed.' }
    & $dotnet restore AgentConsole/AgentConsole.csproj --source .tools/nuget --ignore-failed-sources -p:NuGetAudit=false -v q
    if ($LASTEXITCODE -ne 0) { throw 'Agent Console dependency restore failed.' }
    & $dotnet build AgentConsole/AgentConsole.csproj --no-restore -v q
    if ($LASTEXITCODE -ne 0) { throw 'Agent Console build failed.' }
    if ($CaptureConfig) {
        & $dotnet build Validation/QnbotCompatibility/QnbotCompatibility.csproj --no-restore -v q
        if ($LASTEXITCODE -ne 0) { throw 'AI compatibility host build failed.' }
    }
}
$launchRoot = Join-Path $PSScriptRoot ('Output/AgentRuns/' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $launchRoot | Out-Null
function Quote-Argument([string]$Value) {
    if ($Value.Contains('"')) { throw 'Double quotes are not supported in launch arguments.' }
    return '"' + $Value.TrimEnd('\') + '"'
}
$captureProcess = $null
$consoleProcess = $null
try {
    if ($CaptureConfig) {
        if ($RunDirectory -or $StateDirectory) { throw 'CaptureConfig creates its own run/state pair.' }
        $config = Get-Content -LiteralPath $CaptureConfig -Raw | ConvertFrom-Json
        foreach ($name in @('WindowHandle','BuyerLabel','Prefix','KnowledgeConfig','KnowledgeWorkflow')) {
            if ([string]::IsNullOrWhiteSpace($config.$name)) { throw "CaptureConfig requires $name." }
        }
        $RunDirectory = Join-Path $launchRoot 'capture'
        $StateDirectory = Join-Path $launchRoot 'reply-state'
        $captureArgs = @('bin/Debug/net8.0-windows/win-x64/qnocr-poc.dll', '--agent-context-capture',
            '--database', (Join-Path $launchRoot 'context.db'), '--output', $RunDirectory,
            '--hwnd', $config.WindowHandle, '--prefix', $config.Prefix, '--buyer-label', $config.BuyerLabel,
            '--frames', $(if ($config.Frames) { $config.Frames } else { 600 }),
            '--interval-ms', $(if ($config.IntervalMilliseconds) { $config.IntervalMilliseconds } else { 2000 }),
            '--intent-config', 'Config/intent-provider.json', '--product-state', (Join-Path $launchRoot 'product-state'),
            '--knowledge-config', $config.KnowledgeConfig, '--knowledge-workflow', $config.KnowledgeWorkflow,
            '--reply-state', $StateDirectory, '--ai-provider-config', 'Config/ai-provider.json')
        $captureProcess = Start-Process -FilePath $dotnet -ArgumentList (($captureArgs | ForEach-Object { Quote-Argument "$_" }) -join ' ') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $launchRoot 'capture.log') -RedirectStandardError (Join-Path $launchRoot 'capture.error.log')
        $deadline = (Get-Date).AddSeconds(30)
        while (!(Test-Path -LiteralPath (Join-Path $RunDirectory 'session.json'))) {
            if ($captureProcess.HasExited -or (Get-Date) -gt $deadline) { throw "Capture failed to start. See $launchRoot/capture.error.log" }
            Start-Sleep -Milliseconds 250
        }
    }
    $consoleArgs = @('AgentConsole/bin/Debug/net8.0-windows/win-x64/AgentConsole.dll', '--port', "$Port")
    if ($RunDirectory) {
        $RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
        if (!$StateDirectory) {
            $metadata = Join-Path $RunDirectory 'session.json'
            if (!(Test-Path -LiteralPath $metadata)) { $metadata = Join-Path $RunDirectory 'summary.json' }
            $StateDirectory = (Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json).StateDirectory
        }
        if (!$StateDirectory) { throw 'This run does not declare its reply StateDirectory.' }
        $consoleArgs += @('--state-dir', [IO.Path]::GetFullPath($StateDirectory), '--run-dir', $RunDirectory)
    } else {
        if ($StateDirectory) { throw 'StateDirectory requires RunDirectory.' }
        $consoleArgs += @('--demo-root', (Join-Path $launchRoot 'demo'))
        $RunDirectory = Join-Path $launchRoot 'demo/run'
    }
    $log = Join-Path $launchRoot 'console.log'
    $consoleProcess = Start-Process -FilePath $dotnet -ArgumentList (($consoleArgs | ForEach-Object { Quote-Argument "$_" }) -join ' ') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError (Join-Path $launchRoot 'console.error.log')
    $deadline = (Get-Date).AddSeconds(30)
    $url = $null
    while ((Get-Date) -lt $deadline) {
        if ($consoleProcess.HasExited) { throw "Console failed to start. See $launchRoot/console.error.log" }
        $line = Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Select-String '\[Agent Console\] (http://\S+)' | Select-Object -First 1
        if ($line) {
            $url = $line.Matches[0].Groups[1].Value
            try { $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2; if ($response.StatusCode -eq 200) { break } } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    if (!$url -or !$response -or $response.StatusCode -ne 200) { throw "Console did not become ready. See $launchRoot" }
    $handle = @{ ConsolePid = $consoleProcess.Id; ConsoleStarted = $consoleProcess.StartTime.ToUniversalTime().ToString('O'); RunDirectory = $RunDirectory; Url = $url; LaunchRoot = $launchRoot }
    if ($captureProcess) { $handle.CapturePid = $captureProcess.Id }
    $handle | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $launchRoot 'launch.json') -Encoding UTF8
    Write-Output "READY: $url"
    Write-Output "Logs: $launchRoot"
    Write-Output "Stop: .\Stop-Agent.ps1 -LaunchDirectory '$launchRoot'"
} catch {
    if ($consoleProcess -and !$consoleProcess.HasExited) { $consoleProcess.Kill() }
    if ($captureProcess -and !$captureProcess.HasExited) { $captureProcess.Kill() }
    throw
}
