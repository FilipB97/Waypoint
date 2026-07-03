# Buduje Assets\app.ico z mastera scripts\waypoint-icon.png (256px).
#
# Układ hybrydowy (standard Windows): małe klatki 16-64 px jako nieskompresowany
# BMP (32bpp BGRA + maska AND), duże 128/256 px jako PNG. Powód: System.Drawing.Icon
# / Shell_NotifyIcon (ikona w zasobniku) NIE dekodują klatek PNG-in-ICO i renderują
# je jako szum — dlatego małe rozmiary muszą być BMP, żeby ikona była poprawna wszędzie.
#
# Uruchom: powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$master = Join-Path $PSScriptRoot 'waypoint-icon.png'
$outIco = Join-Path $root 'src\RdpManager\Assets\app.ico'

# 256 px = png (duża, GDI+ ją dekoduje), reszta = bmp (poprawne małe ikony w GDI+/tray)
$specs = @(
    @{ s = 16;  png = $false },
    @{ s = 24;  png = $false },
    @{ s = 32;  png = $false },
    @{ s = 48;  png = $false },
    @{ s = 64;  png = $false },
    @{ s = 128; png = $true  },
    @{ s = 256; png = $true  }
)

function Resize-Bitmap($src, $size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

# Klatka BMP: BITMAPINFOHEADER (biHeight = 2*H, bo obejmuje XOR + maskę AND),
# piksele 32bpp BGRA od dołu do góry, potem zerowa maska AND (alfa robi przezroczystość).
function New-BmpFrame($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1);  $bw.Write([uint16]32)
    $bw.Write([uint32]0);  $bw.Write([uint32]0)
    $bw.Write([int32]0);   $bw.Write([int32]0)
    $bw.Write([uint32]0);  $bw.Write([uint32]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([math]::Floor(($w + 31) / 32) * 4)
    $bw.Write((New-Object byte[] ($rowBytes * $h)))
    $bw.Flush()
    return ,$ms.ToArray()   # przecinek: nie rozwijaj byte[] przy zwrocie z funkcji
}

function New-PngFrame($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

$src = [System.Drawing.Bitmap]::FromFile($master)
$frames = @()
foreach ($spec in $specs) {
    $bmp = Resize-Bitmap $src $spec.s
    $data = if ($spec.png) { New-PngFrame $bmp } else { New-BmpFrame $bmp }
    $frames += @{ size = $spec.s; data = $data }
    $bmp.Dispose()
}
$src.Dispose()

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)   # ICONDIR
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.size -ge 256) { 0 } else { $f.size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1);  $w.Write([uint16]32)
    $w.Write([uint32]$f.data.Length); $w.Write([uint32]$offset)
    $offset += $f.data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f.data) }
$w.Flush()
[System.IO.File]::WriteAllBytes($outIco, $out.ToArray())

$kb = [math]::Round((Get-Item $outIco).Length / 1KB, 1)
Write-Output "Zapisano $outIco ($kb KB, $($frames.Count) klatek: 16-64 BMP, 128/256 PNG)"
