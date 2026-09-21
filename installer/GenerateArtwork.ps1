# Rebuild the installer's original, code-drawn artwork. No external assets or account data.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
$background = [Drawing.Color]::FromArgb(24, 24, 24)
$foreground = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(242, 242, 238))
$muted = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(164, 164, 160))
$line = [Drawing.Pen]::new([Drawing.Color]::FromArgb(62, 62, 62), 2)
$stroke = [Drawing.Pen]::new($foreground.Color, 4)
$font = [Drawing.Font]::new('Segoe UI', 31, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
$small = [Drawing.Font]::new('Segoe UI', 16, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$number = [Drawing.Font]::new('Segoe UI', 80, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
try {
    $bitmap = [Drawing.Bitmap]::new(328, 628)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear($background)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.DrawLines($stroke, [Drawing.Point[]]@([Drawing.Point]::new(36,44), [Drawing.Point]::new(55,56), [Drawing.Point]::new(36,68)))
        $graphics.DrawLine($stroke, 63, 69, 82, 69)
        $graphics.DrawString("Codex`nTracker", $font, $foreground, [Drawing.PointF]::new(30, 109))
        $graphics.DrawLine($line, 36, 235, 292, 235)
        $graphics.DrawString('58', $number, $foreground, [Drawing.PointF]::new(27, 287))
        $graphics.DrawString('%', $font, $muted, [Drawing.PointF]::new(128, 335))
        $graphics.DrawString('WEEKLY QUOTA', $small, $muted, [Drawing.PointF]::new(35, 398))
        $graphics.DrawLine($line, 36, 441, 291, 441)
        $graphics.DrawLine($stroke, 36, 441, 185, 441)
        $graphics.DrawString('WINDOWS  /  LOCAL', $small, $muted, [Drawing.PointF]::new(35, 548))
        $bitmap.Save((Join-Path $assetDirectory 'Welcome.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    $bitmap = [Drawing.Bitmap]::new(110, 110)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear($background)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.DrawLines($stroke, [Drawing.Point[]]@([Drawing.Point]::new(27,36), [Drawing.Point]::new(51,53), [Drawing.Point]::new(27,70)))
        $graphics.DrawLine($stroke, 57, 72, 82, 72)
        $bitmap.Save((Join-Path $assetDirectory 'Mark.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
} finally { $foreground.Dispose(); $muted.Dispose(); $line.Dispose(); $stroke.Dispose(); $font.Dispose(); $small.Dispose(); $number.Dispose() }
