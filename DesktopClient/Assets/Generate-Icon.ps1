$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# Same geometry as qn-agent.svg. Generate native ICO frames for taskbar, title bar and tray.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.ScaleTransform($size / 64.0, $size / 64.0)
    $background = [Drawing.Drawing2D.GraphicsPath]::new()
    $background.AddArc(1, 1, 28, 28, 180, 90)
    $background.AddArc(35, 1, 28, 28, 270, 90)
    $background.AddArc(35, 35, 28, 28, 0, 90)
    $background.AddArc(1, 35, 28, 28, 90, 90)
    $background.CloseFigure()
    $blue = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#176BCE'))
    $graphics.FillPath($blue, $background)
    $bubble = [Drawing.Drawing2D.GraphicsPath]::new()
    $bubble.AddLine(19,12,45,12)
    $bubble.AddBezier(45,12,51,12,54,15,54,21)
    $bubble.AddLine(54,21,54,35)
    $bubble.AddBezier(54,35,54,41,51,44,45,44)
    $bubble.AddLine(45,44,29,44); $bubble.AddLine(29,44,17,52); $bubble.AddLine(17,52,19,44)
    $bubble.AddBezier(19,44,13,44,10,41,10,35)
    $bubble.AddLine(10,35,10,21); $bubble.AddBezier(10,21,10,15,13,12,19,12)
    $bubble.CloseFigure(); $graphics.FillPath([Drawing.Brushes]::White, $bubble)
    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#15A996'), 5)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawLines($pen, [Drawing.PointF[]]@([Drawing.PointF]::new(22,27),[Drawing.PointF]::new(29,34),[Drawing.PointF]::new(43,21)))
    $memory = [IO.MemoryStream]::new(); $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($memory.ToArray())
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'qn-agent.png'), $memory.ToArray()) }
    $memory.Dispose(); $pen.Dispose(); $blue.Dispose(); $background.Dispose(); $bubble.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $PSScriptRoot 'qn-agent.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $encodedSize = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$encodedSize); $writer.Write([byte]$encodedSize); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($bytes in $frames) { $writer.Write($bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output 'Generated QN Agent icon: 16–256 pixel frames.'
