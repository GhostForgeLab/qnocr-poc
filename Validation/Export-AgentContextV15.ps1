param(
    [Parameter(Mandatory)][string]$StateDirectory,
    [Parameter(Mandatory)][string]$SessionId
)
$ErrorActionPreference='Stop'
if($SessionId -notmatch '^[0-9a-fA-F]{64}$'){throw 'A full 64-character SessionId is required.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=(Resolve-Path -LiteralPath $StateDirectory).Path
$session=(Get-Content -LiteralPath (Join-Path $root "sessions/$SessionId/session.json") -Raw|ConvertFrom-Json)
$items=@(Get-ChildItem -LiteralPath (Join-Path $root 'outbox') -Recurse -Filter item.json|ForEach-Object{
    Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json
}|Where-Object{$_.Session.SessionId -eq $SessionId -and $_.State -eq 'Completed'}|Sort-Object CaptureTime)
if($items.Count -eq 0){throw 'No confirmed session messages are available.'}
$latest=$items[-1]
$messages=@($items|ForEach-Object{$_.Envelope.Messages})
$view=[ordered]@{
    SessionId=$session.SessionId;BuyerId=$session.BuyerId;ProductId=$latest.Envelope.ProductId
    Messages=$messages;CapturedAt=$latest.CaptureTime;CaptureId='historical-context-readonly'
    MessageId=$latest.MessageId;AiRequestId='NO_AI_REQUEST_IN_THIS_EXPORT'
    ModelInputMessageCount=[Math]::Min(10,$messages.Count)
    Description='Read-only export of prior real OCR evidence; not a new V1.5 AI request.'
}
$directory=Join-Path $repo 'Output/ValidationV15'
New-Item -ItemType Directory -Force -Path $directory|Out-Null
$path=Join-Path $directory 'real-session-context.json'
$view|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $path -Encoding utf8
[pscustomobject]@{Path=$path;SessionId=$view.SessionId;MessageCount=$messages.Count;ProductId=$view.ProductId;ExternalAiCalls=0}
