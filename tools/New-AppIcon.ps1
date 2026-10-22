# Draw the editable SVG geometry at each Windows icon size. Windows / System.Drawing only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
$images = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.PixelOffsetMode = 'HighQuality'
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $navy = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#14243b'))
    $mint = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#66e3bd'), 18)
    $mint.StartCap = 'Round'; $mint.EndCap = 'Round'
    $tile = [Drawing.Drawing2D.GraphicsPath]::new()
    $tile.AddArc(0, 0, 112, 112, 180, 90)
    $tile.AddArc(144, 0, 112, 112, 270, 90)
    $tile.AddArc(144, 144, 112, 112, 0, 90)
    $tile.AddArc(0, 144, 112, 112, 90, 90)
    $tile.CloseFigure(); $graphics.FillPath($navy, $tile)
    $shackle = [Drawing.Drawing2D.GraphicsPath]::new()
    $shackle.AddLine(88, 126, 88, 88)
    $shackle.AddBezier(88, 88, 88, 38, 168, 38, 168, 88)
    $shackle.AddLine(168, 88, 168, 96)
    $graphics.DrawPath($mint, $shackle)
    $body = [Drawing.Drawing2D.GraphicsPath]::new()
    $body.AddArc(64, 120, 40, 40, 180, 90)
    $body.AddArc(152, 120, 40, 40, 270, 90)
    $body.AddArc(152, 168, 40, 40, 0, 90)
    $body.AddArc(64, 168, 40, 40, 90, 90)
    $body.CloseFigure(); $graphics.FillPath([Drawing.Brushes]::White, $body)
    $graphics.FillPolygon([Drawing.Brushes]::White, [Drawing.PointF[]]@(
        [Drawing.PointF]::new(80, 196), [Drawing.PointF]::new(80, 228), [Drawing.PointF]::new(116, 200)))
    $graphics.FillEllipse($navy, 116, 142, 24, 24)
    $graphics.FillPolygon($navy, [Drawing.PointF[]]@(
        [Drawing.PointF]::new(122, 158), [Drawing.PointF]::new(134, 158),
        [Drawing.PointF]::new(138, 184), [Drawing.PointF]::new(118, 184)))
    $buffer = [IO.MemoryStream]::new()
    $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
    $images += @{ Size = $size; Data = $buffer.ToArray() }
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'unblock-discord.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $buffer.Dispose(); $tile.Dispose(); $shackle.Dispose(); $body.Dispose()
    $mint.Dispose(); $navy.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $assetDirectory 'unblock-discord.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($item in $images) {
        $dimension = if ($item.Size -eq 256) { 0 } else { $item.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$item.Data.Length); $writer.Write([uint32]$offset)
        $offset += $item.Data.Length
    }
    foreach ($item in $images) { $writer.Write([byte[]]$item.Data) }
} finally { $writer.Dispose() }
