param([Parameter(Mandatory)][string]$SessionDirectory)
$ErrorActionPreference = 'Stop'
$session = (Resolve-Path -LiteralPath $SessionDirectory).Path
$frames = @(Get-Content -LiteralPath (Join-Path $session 'shadow.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$receipts = @(Get-ChildItem -LiteralPath $session -Recurse -Filter shadow-adapter-result.json | Sort-Object FullName | ForEach-Object {
    $r = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{
        ReceiptPath=$_.FullName; Status=$r.Status; BuyerMessages=$r.Input; ProductId=$r.ProductId
        SalesAgent=$r.SalesAgent; KnowledgeV2=$r.KnowledgeV2; GenerationTrace=$r.GenerationTrace
        ReplySuggestion=$r.ReplySuggestion; AiInvoked=$r.AiInvoked; AiCalls=$r.AiCalls
        AiDurationMilliseconds=$r.AiDurationMilliseconds; TotalDurationMilliseconds=$r.DurationMilliseconds
        LastAiError=$r.LastAiError; SendEnabled=$r.SendEnabled; SentMessages=$r.SentMessages
        SendEntryInvoked=$r.SendEntryInvoked; ForbiddenBusinessCalls=$r.ForbiddenBusinessCalls
    }
})
$new = @($frames | ForEach-Object { $_.Decisions } | Where-Object { $_.Disposition -eq 'New' })
# Audit only: NFKC exposes width/punctuation variants of the same timestamped message.
# This does not change the production fingerprint or silently repair recorded inputs.
$equivalent = @($receipts | ForEach-Object { $_.BuyerMessages } | ForEach-Object {
    [pscustomobject]@{Key=(ConvertTo-Json -InputObject @($_.Role,$_.Text.Normalize([Text.NormalizationForm]::FormKC),[string]$_.Timestamp) -Compress); Message=$_}
} | Group-Object Key)
$summaryPath = Join-Path $session 'summary.json'
$summary = if (Test-Path -LiteralPath $summaryPath) { Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json } else { $null }
$audit = [ordered]@{
    SessionDirectory=$session; Summary=$summary; ReceiptCount=$receipts.Count
    BuyerMessageCount=@($receipts | ForEach-Object { $_.BuyerMessages }).Count
    SuccessfulPreviews=@($receipts | Where-Object { $_.Status -eq 'Converted' -and $_.ReplySuggestion }).Count
    AiInvokedResults=@($receipts | Where-Object AiInvoked).Count
    AiHttpAttempts=[long]($receipts | Measure-Object AiCalls -Sum).Sum
    DuplicateEmittedFingerprints=$new.Count-@($new.Fingerprint | Sort-Object -Unique).Count
    EquivalentMessageDuplicates=[long](($equivalent | ForEach-Object { $_.Count-1 } | Measure-Object -Sum).Sum)
    EquivalentDuplicateGroups=@($equivalent | Where-Object Count -gt 1 | ForEach-Object { @{Count=$_.Count;Messages=@($_.Group.Message)} })
    KnowledgeV2Matches=[long]($receipts | ForEach-Object { $_.KnowledgeV2.CandidateCount } | Measure-Object -Sum).Sum
    SentMessages=[long]($receipts | Measure-Object SentMessages -Sum).Sum
    SendBoundaryViolations=@($receipts | Where-Object { $_.SendEnabled -or $_.SendEntryInvoked -or $_.SentMessages -ne 0 -or $_.ForbiddenBusinessCalls -ne 0 }).Count
    Results=$receipts
}
$output = Join-Path $session 'acceptance-audit.json'
$audit | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $output -Encoding utf8
[pscustomobject]@{AuditPath=$output;Messages=$audit.BuyerMessageCount;Previews=$audit.SuccessfulPreviews;AiResults=$audit.AiInvokedResults;Sent=$audit.SentMessages;ExactFingerprintDuplicates=$audit.DuplicateEmittedFingerprints;EquivalentMessageDuplicates=$audit.EquivalentMessageDuplicates}
