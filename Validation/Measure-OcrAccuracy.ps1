param(
    [Parameter(Mandatory)][string]$OcrJson,
    [Parameter(Mandatory)][string]$GroundTruthJson
)
$ErrorActionPreference = 'Stop'
function Get-EditDistance([string]$Expected, [string]$Actual) {
    $previous = [int[]](0..$Actual.Length)
    for ($i = 1; $i -le $Expected.Length; $i++) {
        $current = [int[]]::new($Actual.Length + 1)
        $current[0] = $i
        for ($j = 1; $j -le $Actual.Length; $j++) {
            $cost = [int]($Expected[$i - 1] -cne $Actual[$j - 1])
            $current[$j] = [Math]::Min([Math]::Min($previous[$j] + 1, $current[$j - 1] + 1), $previous[$j - 1] + $cost)
        }
        $previous = $current
    }
    return $previous[$Actual.Length]
}
$truth = Get-Content -LiteralPath $GroundTruthJson -Raw | ConvertFrom-Json
$ocr = Get-Content -LiteralPath $OcrJson -Raw | ConvertFrom-Json
$expected = $truth.Lines -join ''
$actual = $ocr.Lines.Text -join ''
if ($expected.Length -eq 0) { throw 'Ground truth cannot be empty.' }
$expectedCompact = $expected -replace '\s', ''
$actualCompact = $actual -replace '\s', ''
$rawDistance = Get-EditDistance $expected $actual
$compactDistance = Get-EditDistance $expectedCompact $actualCompact
$exactLines = 0
for ($i = 0; $i -lt $truth.Lines.Count; $i++) {
    if ($i -lt $ocr.Lines.Count -and $truth.Lines[$i] -ceq $ocr.Lines[$i].Text) { $exactLines++ }
}
$keywords = foreach ($token in @('TA1', '200ml', '304', 'F2U')) {
    $pattern = '(?<![A-Za-z0-9])' + [Regex]::Escape($token) + '(?![A-Za-z0-9])'
    $expectedCount = [Regex]::Matches($expected, $pattern).Count
    $actualCount = [Regex]::Matches($actual, $pattern).Count
    [PSCustomObject]@{ Token = $token; ExpectedOccurrences = $expectedCount; RecognizedOccurrences = $actualCount }
}
[PSCustomObject]@{
    OcrJson = [IO.Path]::GetFullPath($OcrJson)
    GroundTruthJson = [IO.Path]::GetFullPath($GroundTruthJson)
    ReferenceCharacters = $expected.Length
    RawEditDistance = $rawDistance
    RawCharacterErrorRate = $rawDistance / $expected.Length
    WhitespaceExcludedReferenceCharacters = $expectedCompact.Length
    WhitespaceExcludedEditDistance = $compactDistance
    WhitespaceExcludedCharacterErrorRate = $compactDistance / $expectedCompact.Length
    ExactLines = $exactLines
    ReferenceLines = $truth.Lines.Count
    Keywords = @($keywords)
    Note = 'Line breaks excluded; raw metric keeps spaces/case/punctuation. Second metric removes only whitespace. No full-width punctuation normalization.'
} | ConvertTo-Json -Depth 5
