param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [string]$ConsoleLog = 'Output/v110-long-run.log',
    [string]$OutputFile = 'Output/ValidationV110/long-run-analysis.json'
)
$ErrorActionPreference = 'Stop'
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$summaryPath = Join-Path $run 'summary.json'
if (!(Test-Path -LiteralPath $summaryPath)) { throw 'The run has not written summary.json yet.' }
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
function Read-Rows([string]$stem) {
    $files = @(Get-ChildItem -LiteralPath $run -Recurse -File | Where-Object {
        $_.Name -eq "$stem.jsonl" -or $_.Name -like "$stem.*.jsonl"
    } | Sort-Object FullName)
    foreach ($file in $files) {
        Get-Content -LiteralPath $file.FullName | Where-Object { $_.Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json }
    }
}
$health = @(Read-Rows 'health' | Sort-Object At)
$shadow = @(Read-Rows 'shadow')
$life = @(Read-Rows 'lifecycle')
$sessions = @(Read-Rows 'session-lifecycle')
$agent = @(Read-Rows 'agent-pipeline')
$recovery = @(Read-Rows 'recovery')
$attempts = @($life | Where-Object Stage -eq 'Attempt')
$retryIds = @($attempts | Group-Object MessageId | Where-Object Count -gt 1 | Select-Object -ExpandProperty Name)
$crossed = @($agent | Where-Object Event -eq 'MESSAGE_ATTACHED' | Group-Object MessageId | Where-Object {
    @($_.Group.SessionId | Sort-Object -Unique).Count -gt 1
})
$consoleText = if (Test-Path -LiteralPath $ConsoleLog) { Get-Content -LiteralPath $ConsoleLog -Raw } else { '' }
$result = [ordered]@{
    RunDirectory = $run
    Status = $summary.Status
    StartedAt = $summary.StartedAt
    EndedAt = $summary.EndedAt
    DurationSeconds = [math]::Round([double]$summary.ElapsedSeconds, 3)
    ReachedTwoHours = [double]$summary.ElapsedSeconds -ge 7200
    Attempts = $summary.Attempts
    SuccessfulFrames = $summary.SuccessfulFrames
    FailedFrames = $summary.FailedFrames
    ConfirmedObservations = $summary.ConfirmedObservations
    NewMessages = $summary.NewMessages
    BuyerSessions = $summary.BuyerSessions
    PersistedSessions = $summary.PersistedSessions
    ProductReferences = $summary.ProductReferences
    AiCalls = $summary.AiCalls
    ReplyPreviews = $summary.ReplyPreviews
    SentMessages = $summary.SentMessages
    ErrorCount = $summary.ErrorCount
    PeakWorkingSetBytes = $summary.PeakWorkingSetBytes
    FirstWorkingSetBytes = if ($health.Count) { $health[0].WorkingSetBytes } else { $null }
    LastWorkingSetBytes = if ($health.Count) { $health[-1].WorkingSetBytes } else { $null }
    RunBytes = $summary.RunBytes
    StateBytes = $summary.StateBytes
    HealthSamples = $health.Count
    LogArchiveSegments = $summary.LogArchiveSegments
    SessionOpened = @($sessions | Where-Object Event -eq 'SESSION_RUN_OPENED').Count
    SessionResumed = @($sessions | Where-Object Event -eq 'SESSION_RUN_RESUMED').Count
    SessionClosed = @($sessions | Where-Object Event -eq 'SESSION_RUN_CLOSED').Count
    DispatchAttempts = $attempts.Count
    RetriedMessageIds = $retryIds
    CrossSessionMessageIds = @($crossed | Select-Object -ExpandProperty Name)
    UnhandledExceptionInConsole = $consoleText -match 'Unhandled exception|未处理的异常'
    RecoveryStages = @($recovery | Group-Object Stage | ForEach-Object { [pscustomobject]@{ Stage = $_.Name; Count = $_.Count } })
}
$target = [IO.Path]::GetFullPath($OutputFile)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $target -Encoding utf8
[pscustomobject]@{ AnalysisPath = $target; Status = $result.Status; DurationSeconds = $result.DurationSeconds;
    Frames = $result.SuccessfulFrames; Errors = $result.ErrorCount; SentMessages = $result.SentMessages }
