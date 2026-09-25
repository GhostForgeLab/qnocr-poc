param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$run=(Resolve-Path -LiteralPath $RunDirectory).Path
$meta=Get-Content -LiteralPath (Join-Path $run 'session.json') -Raw | ConvertFrom-Json
$health=@(Get-Content -LiteralPath (Join-Path $run 'health.jsonl') | ForEach-Object{$_|ConvertFrom-Json})
$events=@(Get-Content -LiteralPath (Join-Path $run 'shadow.jsonl') | ForEach-Object{$_|ConvertFrom-Json})
$lifePath=Join-Path $run 'lifecycle.jsonl'
$life=if(Test-Path -LiteralPath $lifePath){@(Get-Content -LiteralPath $lifePath|ForEach-Object{$_|ConvertFrom-Json})}else{@()}
$persisted=@($life|Where-Object Stage -eq 'Persisted'|Select-Object -ExpandProperty MessageId -Unique)
$items=@(foreach($id in $persisted){
    $path=Join-Path $meta.StateDirectory "outbox/$id/item.json"
    $item=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    $receipt=if($item.ReceiptPath -and (Test-Path -LiteralPath $item.ReceiptPath)){Get-Content -LiteralPath $item.ReceiptPath -Raw|ConvertFrom-Json}else{$null}
    [pscustomobject]@{MessageId=$id;SessionId=$item.Session.SessionId;Buyer=$item.Session.ObservedBuyer;ProductId=$item.Envelope.ProductId;
        Input=$item.Envelope.Messages;State=$item.State;Attempts=$item.Attempts;ReceiptPath=$item.ReceiptPath;Receipt=$receipt}
})
$equivalent=@($items|ForEach-Object{$i=$_;foreach($m in $_.Input){
    [pscustomobject]@{Key=(ConvertTo-Json -InputObject @($i.SessionId,$m.Role,$m.Text.Normalize([Text.NormalizationForm]::FormKC),[string]$m.Timestamp) -Compress)}
}}|Group-Object Key)
$summaryPath=Join-Path $run 'summary.json'
$summary=if(Test-Path -LiteralPath $summaryPath){Get-Content -LiteralPath $summaryPath -Raw|ConvertFrom-Json}else{$null}
$audit=[ordered]@{
    RunDirectory=$run;Summary=$summary;LatestHealth=$health[-1];HealthRecords=$health.Count
    PersistedMessages=$items.Count;Completed=@($items|Where-Object State -eq 'Completed').Count
    Pending=@($items|Where-Object State -eq 'Pending').Count;Uncertain=@($items|Where-Object State -eq 'Uncertain').Count
    EquivalentDuplicateMessages=[long](($equivalent|ForEach-Object{$_.Count-1}|Measure-Object -Sum).Sum)
    SuppressedDuplicateObservations=@($life|Where-Object{$_.Stage -in 'DuplicateFingerprint','ContextChangedOnly','OuterDuplicate'}).Count
    AdapterFailures=@($life|Where-Object{$_.Stage -in 'Uncertain','Rejected','ConfigurationHold'}).Count
    SentMessages=[long](($items.Receipt|Measure-Object SentMessages -Sum).Sum)
    SendBoundaryViolations=@($items|Where-Object{$_.Receipt -and ($_.Receipt.SendEnabled -or $_.Receipt.SendEntryInvoked -or $_.Receipt.ForbiddenBusinessCalls -ne 0)}).Count
    ParentMemoryMinBytes=($health|Measure-Object WorkingSetBytes -Minimum).Minimum
    ParentMemoryMaxBytes=($health|Measure-Object WorkingSetBytes -Maximum).Maximum
    Results=$items
}
$output=Join-Path $run 'stability-audit.json'
$audit|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $output -Encoding utf8
[pscustomobject]@{Audit=$output;ElapsedSeconds=$summary.ElapsedSeconds;Messages=$items.Count;Completed=$audit.Completed;Duplicates=$audit.EquivalentDuplicateMessages;Sent=$audit.SentMessages}
