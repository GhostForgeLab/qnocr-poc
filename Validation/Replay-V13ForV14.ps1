param([Parameter(Mandatory)][string]$SessionDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'bin/Debug/net8.0-windows/win-x64/qnocr-poc.dll'))
$session=(Resolve-Path -LiteralPath $SessionDirectory).Path
$meta=Get-Content -LiteralPath (Join-Path $session 'session.json') -Raw|ConvertFrom-Json
$start=[datetime]$meta.ObservationStart
$tracker=[Activator]::CreateInstance($assembly.GetType('QnocrPoc.Shadow.NewMessageTracker'), @($start))
$batchType=$assembly.GetType('QnocrPoc.Models.BuyerMessageBatch')
$new=[Collections.Generic.List[object]]::new()
$decisions=[Collections.Generic.List[object]]::new()
foreach($frame in (Get-Content -LiteralPath (Join-Path $session 'shadow.jsonl')|ForEach-Object{$_|ConvertFrom-Json})){
    if(!$frame.EventDirectory){continue}
    $path=Join-Path $frame.EventDirectory 'buyer-messages.json'
    if(!(Test-Path -LiteralPath $path)){continue}
    $batch=[Text.Json.JsonSerializer]::Deserialize([IO.File]::ReadAllText($path),$batchType,[Text.Json.JsonSerializerOptions]$null)
    $comparison=$tracker.Compare($batch.Messages,([datetimeoffset]$frame.CapturedAt).DateTime)
    foreach($m in $comparison.NewMessages){$new.Add([pscustomobject]@{Frame=$frame.Frame;Text=$m.Text;Timestamp=$m.Timestamp;ProductId=$m.ProductId})}
    foreach($d in $comparison.Decisions){$decisions.Add([pscustomobject]@{Frame=$frame.Frame;Disposition=$d.Disposition;Fingerprint=$d.Fingerprint})}
}
$output=Join-Path $repo 'Output/v14-v13-replay.json'
@{Kind='Offline replay of actual V1.3 OCR evidence, not new live messages';InputSession=$session;ObservationStart=$start;NewCount=$new.Count;NewMessages=@($new.ToArray());Decisions=@($decisions.ToArray())}|
    ConvertTo-Json -Depth 8|Set-Content -LiteralPath $output -Encoding utf8
[pscustomobject]@{Output=$output;NewCount=$new.Count;DistinctTextTimestamp=@($new|Group-Object Text,Timestamp).Count;BeforePcBaseline=@($new|Where-Object{$_.Timestamp -lt $start}).Count}
if($new.Count -ne 11 -or @($new|Group-Object Text,Timestamp).Count -ne 11){throw 'V1.3 replay regression: expected original ten plus one genuine specification resend, without OCR punctuation duplicates.'}
