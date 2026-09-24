param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Output\OCR\Fixtures'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$lines = @(
    '买家: 这个200ml还有货吗?',
    '客服: 有现货，型号TA1。',
    '材质: 304不锈钢，型号F2U。',
    'English: TA1 F2U 200ml 304',
    '容量: 200ml 500mL 1L 1.5L',
    '数量: 2件，价格: ￥39.90',
    '符号: + - / ( ) % # @'
)
foreach ($pixels in @(18, 28)) {
    $bitmap = [Drawing.Bitmap]::new(950, 420)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $font = [Drawing.Font]::new('Microsoft YaHei', $pixels, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    try {
        if ($font.Name -notin @('Microsoft YaHei', '微软雅黑')) { throw 'Microsoft YaHei font is required for reproducibility.' }
        $graphics.Clear([Drawing.Color]::White)
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $graphics.DrawString($lines[$i], $font, [Drawing.Brushes]::Black, 24, (20 + $i * 55))
        }
        $name = "mixed_characters_${pixels}px"
        $bitmap.Save((Join-Path $OutputDirectory "$name.png"), [Drawing.Imaging.ImageFormat]::Png)
        @{ Font = $font.Name; FontPixels = $pixels; Lines = $lines } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory "$name.ground-truth.json") -Encoding utf8
        Write-Output (Join-Path $OutputDirectory "$name.png")
    } finally {
        $font.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
$blank = [Drawing.Bitmap]::new(400, 200)
$blankGraphics = [Drawing.Graphics]::FromImage($blank)
try {
    $blankGraphics.Clear([Drawing.Color]::White)
    $blank.Save((Join-Path $OutputDirectory 'blank.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally {
    $blankGraphics.Dispose()
    $blank.Dispose()
}
