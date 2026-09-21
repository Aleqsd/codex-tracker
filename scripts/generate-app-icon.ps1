$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 64.0, $size / 64.0)
    $tile = [Drawing.Drawing2D.GraphicsPath]::new()
    $tile.AddArc(5, 5, 24, 24, 180, 90)
    $tile.AddArc(35, 5, 24, 24, 270, 90)
    $tile.AddArc(35, 35, 24, 24, 0, 90)
    $tile.AddArc(5, 35, 24, 24, 90, 90)
    $tile.CloseFigure()
    $fill = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(32, 32, 32))
    $edge = [Drawing.Pen]::new([Drawing.Color]::FromArgb(73, 73, 73), 1.0)
    $glyph = [Drawing.Pen]::new([Drawing.Color]::FromArgb(239, 239, 236), 2.7)
    $glyph.StartCap = $glyph.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $glyph.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $graphics.FillPath($fill, $tile)
    $graphics.DrawPath($edge, $tile)
    $graphics.DrawLines($glyph, [Drawing.PointF[]]@([Drawing.PointF]::new(21, 23), [Drawing.PointF]::new(31, 32), [Drawing.PointF]::new(21, 41)))
    $graphics.DrawLine($glyph, 35, 41, 44, 41)
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($stream.ToArray())
    $stream.Dispose(); $glyph.Dispose(); $edge.Dispose(); $fill.Dispose(); $tile.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
# PNG-backed ICO frames preserve alpha and native sizes without temporary native icon handles.
$destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\CodexTracker.App\Assets\App.ico'
$output = [IO.File]::Create($destination)
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $output.Dispose() }
