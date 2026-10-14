<#
.SYNOPSIS
    Genera el ícono de NexScreen (.ico con varios tamaños en PNG) a partir de un dibujo vectorial.
    El mismo diseño que el ícono de la app Android: un monitor celeste sobre fondo oscuro.
.EXAMPLE
    host\tools\make-icon.ps1 -Output host\src\NexScreen.Tray\Assets\nexscreen.ico
#>
param(
    [Parameter(Mandatory)] [string]$Output
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 108.0   # mismo lienzo que ic_launcher.xml (108x108)

    function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = 2 * $r
        $path.AddArc($x, $y, $d, $d, 180, 90); $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $path.CloseFigure(); return $path
    }

    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x1F, 0x29, 0x37))
    $blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x60, 0xA5, 0xFA))
    $g.FillPath($dark, (RoundedRect 0 0 $size $size (20 * $s)))
    $g.FillPath($blue, (RoundedRect (18 * $s) (28 * $s) (72 * $s) (44 * $s) (5 * $s)))   # marco del monitor
    $g.FillRectangle($dark, 23 * $s, 33 * $s, 62 * $s, 34 * $s)                          # pantalla
    $g.FillRectangle($blue, 48 * $s, 72 * $s, 12 * $s, 8 * $s)                           # pie
    $g.FillPath($blue, (RoundedRect (36 * $s) (80 * $s) (36 * $s) (6 * $s) (3 * $s)))   # base

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bitmap.Dispose()
    return , $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) { , (New-IconPng $size) }

# Formato ICO: ICONDIR (6 bytes) + ICONDIRENTRY (16 bytes) por imagen + datos PNG.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path (Get-Location) $Output), $out.ToArray())
Write-Host "Ícono generado: $Output ($($out.Length) bytes)"
