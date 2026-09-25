param([Parameter(Mandatory)][string]$QnbotRoot)
$ErrorActionPreference = 'Stop'
$QnbotRoot = (Resolve-Path -LiteralPath $QnbotRoot).Path
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$destination = Join-Path $projectRoot '.tools\qnbot-compat-source'
$head = (& git -C $QnbotRoot -c "safe.directory=$QnbotRoot" rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine qnbot revision.' }
$baseline = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'qnbot-source-baseline.json') -Raw | ConvertFrom-Json
if ($head -ne $baseline.QnbotCommit) { throw 'qnbot HEAD differs from the analyzed baseline. Re-analyze before updating the pinned manifest.' }
$status = @(& git -C $QnbotRoot -c "safe.directory=$QnbotRoot" status --porcelain)
if ($LASTEXITCODE -ne 0 -or $status.Count -gt 0) { throw 'This verified snapshot requires a clean qnbot worktree; do not silently change the baseline.' }
$files = @('src/Bot/ChatRecord/ChatResponse.cs','src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs',
    'src/Bot/ChromeNs/BuyerSessionAgent.cs','src/Bot/ChromeNs/IncomingMessageSafety.cs',
    'src/Bot/ChromeNs/VisionMessageDecision.cs','src/BotLib/Extensions/RegexEx.cs')
$manifest = foreach ($relative in $files) {
    $source = Join-Path $QnbotRoot $relative
    $target = Join-Path $destination $relative
    $before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ($before -ne ($baseline.Sources | Where-Object Path -eq $relative).Sha256) { throw "Source differs from analyzed baseline: $relative" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $before -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $before) { throw 'Source changed during snapshot.' }
    [pscustomobject]@{ Path = $relative; Sha256 = $before }
}
if ((& git -C $QnbotRoot -c "safe.directory=$QnbotRoot" rev-parse HEAD).Trim() -ne $head) { throw 'qnbot revision changed during snapshot.' }
@{ QnbotCommit = $head; Sources = @($manifest); UnmodifiedSourceCopies = $true } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $destination 'source-manifest.json') -Encoding utf8
Write-Output "Read-only qnbot snapshot: $head; $($files.Count) byte-identical files copied to $destination"
