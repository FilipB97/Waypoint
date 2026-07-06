# Generates site/og-image.png (1200x630) - the social preview (Open Graph / Twitter Card).
# Dark site-matching background + cobalt glow + logo tile + wordmark + tagline.
# ASCII-only source (PS 5.1 reads BOM-less .ps1 as ANSI); special glyphs built via [char] at runtime.
# Run: & "scripts\make-og-image.ps1"

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$logo = Join-Path $root 'logo-export\icon\waypoint-512.png'
$out  = Join-Path $root 'site\og-image.png'

$mid  = [char]0x00B7   # middot
$dash = [char]0x2014   # em dash

$W = 1200; $H = 630
$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# Background (ink) - same as --bg on the site.
$g.Clear([System.Drawing.Color]::FromArgb(255, 15, 16, 20))

# Cobalt glow top-right (PathGradientBrush: bright center -> transparent edge).
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddEllipse([single]520, [single](-260), [single]980, [single]760)
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(150, 38, 87, 214)
$pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 38, 87, 214))
$g.FillPath($pgb, $path)
$pgb.Dispose(); $path.Dispose()

# Logo tile on the left, vertically centered.
$logoSize = 248
$logoX = 104; $logoY = [int](($H - $logoSize) / 2)
if (Test-Path $logo) {
    $src = [System.Drawing.Bitmap]::FromFile($logo)
    $g.DrawImage($src, $logoX, $logoY, $logoSize, $logoSize)
    $src.Dispose()
}

$textX = $logoX + $logoSize + 56
$white  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 241, 242, 244))
$muted  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 157, 160, 168))
$accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 58, 107, 232))
$dim    = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 108, 111, 120))

$fTitle = New-Object System.Drawing.Font("Segoe UI", 76, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fSub   = New-Object System.Drawing.Font("Segoe UI", 30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fChip  = New-Object System.Drawing.Font("Segoe UI Semibold", 25, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fFoot  = New-Object System.Drawing.Font("Segoe UI", 23, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

$g.DrawString("Waypoint", $fTitle, $white, [single]($textX - 4), [single]168)
$g.FillRectangle($accent, [single]$textX, [single]280, [single]132, [single]7)
$g.DrawString("Modern RDP & SSH manager for Windows", $fSub, $muted, [single]$textX, [single]308)
$g.DrawString("RDP  $mid  SSH  $mid  SFTP  $mid  Telnet  $mid  Serial", $fChip, $accent, [single]$textX, [single]366)
$g.DrawString("github.com/FilipB97/Waypoint   $dash   free & open source", $fFoot, $dim, [single]$textX, [single]476)

$g.Dispose()
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $out)) | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$kb = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Output ("Saved " + $out + " (" + $kb + " KB, " + $W + "x" + $H + ")")
