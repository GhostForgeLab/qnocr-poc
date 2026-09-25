param(
    [string]$RuntimeBin = 'D:/CodexWork/qnagent/run-native-inbound-shadow-v0259/4802807/Bin',
    [string]$DataRoot = 'C:/Users/LPH/AppData/Local/QianniuAiBot'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$target = Join-Path $projectRoot '.tools/ai-shadow'
if (Test-Path (Join-Path $target 'manifest.json')) { throw 'AI snapshot already exists; use the existing snapshot or explicitly choose a fresh preparation directory.' }
New-Item -ItemType Directory -Force -Path (Join-Path $target 'runtime'),(Join-Path $target 'data-root/data') | Out-Null
# Exact released qnbot binary for the analyzed source revision; never start Bot.exe.
$expected = 'DB400511A3B1761F5FD1935A6143901DF684D0E7FDDAE41A2106CF519C77089B'
if ((Get-FileHash (Join-Path $RuntimeBin 'Bot.exe')).Hash -ne $expected) { throw 'Unexpected qnbot runtime binary; re-audit before changing this pin.' }
Copy-Item -Path (Join-Path $RuntimeBin '*.dll') -Destination (Join-Path $target 'runtime')
Copy-Item -LiteralPath (Join-Path $RuntimeBin 'Bot.exe') -Destination (Join-Path $target 'runtime/Bot.exe')
Copy-Item -LiteralPath (Join-Path $RuntimeBin 'default-business-policy.json') -Destination (Join-Path $target 'runtime/default-business-policy.json')
foreach ($arch in @('x64','x86')) {
    if (Test-Path (Join-Path $RuntimeBin $arch)) { Copy-Item -LiteralPath (Join-Path $RuntimeBin $arch) -Destination (Join-Path $target 'runtime') -Recurse }
}
foreach ($db in @('params.db','bot.db')) {
    $source = Join-Path $DataRoot "data/$db"
    if (!(Test-Path $source)) { throw "Required data file missing: $db" }
    if (Test-Path "$source-wal") { throw 'Live WAL database found; obtain a consistent backup first.' }
    $before = (Get-FileHash $source).Hash
    Copy-Item -LiteralPath $source -Destination (Join-Path $target "data-root/data/$db")
    if ((Get-FileHash $source).Hash -ne $before) { throw 'Data changed during snapshot; retry only with a consistent source.' }
}
# Shop configuration and knowledge stay local, ignored by Git; runtime paths are redirected before settings initialize.
foreach ($folder in @('shops','global')) {
    $source = Join-Path $DataRoot $folder
    if (Test-Path $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $target 'data-root') -Recurse }
}
$files = Get-ChildItem (Join-Path $target 'runtime') -File -Recurse | ForEach-Object {
    @{ Path = [IO.Path]::GetRelativePath((Join-Path $target 'runtime'), $_.FullName); Sha256 = (Get-FileHash $_.FullName).Hash }
}
@{ QnbotCommit='4802807f26a6abf4cfb4457d5b158b2f51f4ff18'; RuntimeFiles=@($files); OriginalDataRoot=$DataRoot; SendEnabled=$false } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $target 'manifest.json') -Encoding utf8
Write-Output 'Prepared local isolated runtime/configuration copy. No keys or settings values printed. Bot.exe was not started.'
