param([string]$ReplayJson='Output/v14-v13-replay.json')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inputData=Get-Content -LiteralPath $ReplayJson -Raw|ConvertFrom-Json
$output=Join-Path $root ('Output/ValidationV14Replay/dispatch_'+(Get-Date -Format yyyyMMdd_HHmmss))
New-Item -ItemType Directory -Force -Path $output|Out-Null
$hostExe=Join-Path $root 'Validation/QnbotCompatibility/bin/Debug/net48/QnbotCompatibility.exe'
$identity=Join-Path $root 'Validation/QnbotCompatibility/identity_mapping.json'
$index=0
$results=@(foreach($m in $inputData.NewMessages){
    $folder=Join-Path $output ('message_'+(++$index).ToString('D3'))
    New-Item -ItemType Directory -Force -Path $folder|Out-Null
    $envelope=Join-Path $folder 'envelope.json'
    @{Source='Qianniu.OCR';SellerId='seller-test';BuyerId='buyer-test';ProductId=$m.ProductId;SchemaVersion=1;
        ProductBinding='OfflineReplayOfRealOcrNotNewLiveArrival';Messages=@(@{Role='buyer';Text=$m.Text;Timestamp=([datetime]$m.Timestamp).ToString('yyyy-MM-ddTHH:mm:ss')})}|
        ConvertTo-Json -Depth 5|Set-Content -LiteralPath $envelope -Encoding utf8
    & $hostExe --dispatch-shadow $envelope $identity $folder > (Join-Path $folder 'host.log') 2>&1
    if($LASTEXITCODE -ne 0){throw "Replay dispatch failed at item $index"}
    $r=Get-Content -LiteralPath (Join-Path $folder 'shadow-adapter-result.json') -Raw|ConvertFrom-Json
    if($r.DispatchResult -ne 'Accepted' -or $r.SentMessages -ne 0 -or $r.AiInvoked -or $r.ForbiddenBusinessCalls -ne 0){throw 'Replay boundary/result mismatch'}
    [pscustomobject]@{Index=$index;OriginFrame=$m.Frame;Text=$m.Text;Timestamp=$m.Timestamp;Receipt=$r}
})
$report=Join-Path $output 'summary.json'
@{Mode='OfflineReplayOfRealV13Ocr; not V1.4 fresh live message test';Count=$results.Count;Accepted=$results.Count;Sent=0;AiCalls=0;Results=$results}|
    ConvertTo-Json -Depth 8|Set-Content -LiteralPath $report -Encoding utf8
[pscustomobject]@{Summary=$report;Count=$results.Count;Accepted=$results.Count;Sent=0;AiCalls=0}
