<#
.SYNOPSIS
    Generates the Windows application icon at assets/branding/logo/metalforge.ico.

.DESCRIPTION
    The icon is a branding asset and must be reproducible, not an opaque binary.
    Brand colours and geometry are written here as readable code, so changing the
    palette lets anyone re-export the icon in one command. The generated .ico is
    committed to the repository so builds never depend on this script.

.NOTES
    ENCODING RULE: this file must stay pure ASCII.
    Windows PowerShell 5.1 decodes BOM-less .ps1 files using the system ANSI code
    page, which corrupts non-ASCII text and breaks parsing. Any script using
    non-ASCII characters must be launched through a self-elevating bootstrap
    (see scripts/Invoke-RepoScript.ps1) or kept ASCII-only like this one.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\assets\branding\logo\metalforge.ico'),
    [int[]]  $Sizes = @(16, 24, 32, 48, 64, 128, 256)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Brand palette: keep in sync with assets/themes/metalforge-dark.theme.json
$palette = @{
    Background = [System.Drawing.ColorTranslator]::FromHtml('#16181C')
    Border     = [System.Drawing.ColorTranslator]::FromHtml('#2A2F36')
    IngotTop   = [System.Drawing.ColorTranslator]::FromHtml('#8FB4D9')
    IngotBase  = [System.Drawing.ColorTranslator]::FromHtml('#5C7FA6')
    AnvilTop   = [System.Drawing.ColorTranslator]::FromHtml('#3A4048')
    AnvilBase  = [System.Drawing.ColorTranslator]::FromHtml('#24282E')
    Spark      = [System.Drawing.ColorTranslator]::FromHtml('#C8A87A')
}

function New-IconBitmap {
    param([int] $Size)

    $scale = $Size / 256.0
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.Clear([System.Drawing.Color]::Transparent)

        # Rounded background plate
        $radius = 56 * $scale
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc(0, 0, $radius * 2, $radius * 2, 180, 90)
        $path.AddArc($Size - ($radius * 2), 0, $radius * 2, $radius * 2, 270, 90)
        $path.AddArc($Size - ($radius * 2), $Size - ($radius * 2), $radius * 2, $radius * 2, 0, 90)
        $path.AddArc(0, $Size - ($radius * 2), $radius * 2, $radius * 2, 90, 90)
        $path.CloseFigure()
        $backgroundBrush = New-Object System.Drawing.SolidBrush($palette.Background)
        $graphics.FillPath($backgroundBrush, $path)

        if ($Size -ge 32) {
            $borderPen = New-Object System.Drawing.Pen($palette.Border, [float](2 * $scale))
            $graphics.DrawPath($borderPen, $path)
            $borderPen.Dispose()
        }

        # Anvil: the "bare metal" metaphor
        $anvilPoints = @(
            (New-Object System.Drawing.PointF((56 * $scale), (150 * $scale))),
            (New-Object System.Drawing.PointF((200 * $scale), (150 * $scale))),
            (New-Object System.Drawing.PointF((200 * $scale), (166 * $scale))),
            (New-Object System.Drawing.PointF((162 * $scale), (166 * $scale))),
            (New-Object System.Drawing.PointF((156 * $scale), (192 * $scale))),
            (New-Object System.Drawing.PointF((180 * $scale), (192 * $scale))),
            (New-Object System.Drawing.PointF((180 * $scale), (202 * $scale))),
            (New-Object System.Drawing.PointF((104 * $scale), (202 * $scale))),
            (New-Object System.Drawing.PointF((104 * $scale), (192 * $scale))),
            (New-Object System.Drawing.PointF((128 * $scale), (192 * $scale))),
            (New-Object System.Drawing.PointF((122 * $scale), (166 * $scale))),
            (New-Object System.Drawing.PointF((56 * $scale), (166 * $scale)))
        )
        $anvilBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF(0, (150 * $scale))),
            (New-Object System.Drawing.PointF(0, (202 * $scale))),
            $palette.AnvilTop,
            $palette.AnvilBase)
        $graphics.FillPolygon($anvilBrush, $anvilPoints)

        # Ingot being forged (tilted rounded rectangle)
        $state = $graphics.Save()
        $graphics.TranslateTransform(128 * $scale, 108 * $scale)
        $graphics.RotateTransform(-12)
        $ingotRadius = 10 * $scale
        $ingotPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $ingotWidth = 100 * $scale
        $ingotHeight = 44 * $scale
        $left = -$ingotWidth / 2
        $top = -$ingotHeight / 2
        $ingotPath.AddArc($left, $top, $ingotRadius * 2, $ingotRadius * 2, 180, 90)
        $ingotPath.AddArc(($left + $ingotWidth - ($ingotRadius * 2)), $top, $ingotRadius * 2, $ingotRadius * 2, 270, 90)
        $ingotPath.AddArc(($left + $ingotWidth - ($ingotRadius * 2)), ($top + $ingotHeight - ($ingotRadius * 2)), $ingotRadius * 2, $ingotRadius * 2, 0, 90)
        $ingotPath.AddArc($left, ($top + $ingotHeight - ($ingotRadius * 2)), $ingotRadius * 2, $ingotRadius * 2, 90, 90)
        $ingotPath.CloseFigure()
        $ingotBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF($left, $top)),
            (New-Object System.Drawing.PointF(($left + $ingotWidth), ($top + $ingotHeight))),
            $palette.IngotTop,
            $palette.IngotBase)
        $graphics.FillPath($ingotBrush, $ingotPath)
        $graphics.Restore($state)

        # Sparks: the build/flash action
        if ($Size -ge 24) {
            $sparkBrush = New-Object System.Drawing.SolidBrush($palette.Spark)
            foreach ($spark in @(@(186, 70, 4.5), @(206, 90, 3.0), @(172, 52, 2.5))) {
                $diameter = [float]($spark[2] * 2 * $scale)
                $graphics.FillEllipse(
                    $sparkBrush,
                    [float](($spark[0] * $scale) - ($diameter / 2)),
                    [float](($spark[1] * $scale) - ($diameter / 2)),
                    $diameter,
                    $diameter)
            }
        }

        return $bitmap
    }
    finally {
        $graphics.Dispose()
    }
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap] $Bitmap)
    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return [byte[]]$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if (-not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$frames = @()
foreach ($size in $Sizes) {
    $bitmap = New-IconBitmap -Size $size
    try {
        $frames += [pscustomobject]@{ Size = $size; Bytes = (ConvertTo-PngBytes -Bitmap $bitmap) }
    }
    finally {
        $bitmap.Dispose()
    }
}

# Assemble the ICO container by writing into one byte[] with explicit offsets.
# Deliberately NOT using BinaryWriter + MemoryStream: BinaryWriter takes ownership
# of the stream on Dispose, and PowerShell's overload resolution for Write() is
# easy to get subtly wrong. Explicit indexing has neither failure mode.
$payloadLength = 0
foreach ($frame in $frames) { $payloadLength += $frame.Bytes.Length }

$totalLength = 6 + (16 * $frames.Count) + $payloadLength
$buffer = New-Object byte[] $totalLength

# ICONDIR
$buffer[0] = 0
$buffer[1] = 0
$buffer[2] = 1                                                      # type = icon
$buffer[3] = 0
$buffer[4] = [byte]($frames.Count -band 0xFF)                       # image count, uint16 LE
$buffer[5] = [byte](($frames.Count -shr 8) -band 0xFF)

# ICONDIRENTRY per frame, followed by frame payloads
$offset = 6 + (16 * $frames.Count)
for ($index = 0; $index -lt $frames.Count; $index++) {
    $frame = $frames[$index]
    $entry = 6 + ($index * 16)
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }  # 256 is stored as 0

    $buffer[$entry + 0] = [byte]$dimension                          # width
    $buffer[$entry + 1] = [byte]$dimension                          # height
    $buffer[$entry + 2] = 0                                         # palette count
    $buffer[$entry + 3] = 0                                         # reserved
    $buffer[$entry + 4] = 1                                         # colour planes, uint16 LE
    $buffer[$entry + 5] = 0
    $buffer[$entry + 6] = 32                                        # bits per pixel, uint16 LE
    $buffer[$entry + 7] = 0
    [Array]::Copy([BitConverter]::GetBytes([uint32]$frame.Bytes.Length), 0, $buffer, $entry + 8, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]$offset), 0, $buffer, $entry + 12, 4)

    [Array]::Copy($frame.Bytes, 0, $buffer, $offset, $frame.Bytes.Length)
    $offset += $frame.Bytes.Length
}

if ($offset -ne $totalLength) {
    throw ("Internal error: write position {0} does not match expected length {1}." -f $offset, $totalLength)
}

[System.IO.File]::WriteAllBytes($resolvedOutput, $buffer)

# Read back and verify. "It reported success" is not evidence that it worked.
$written = [System.IO.File]::ReadAllBytes($resolvedOutput)
if ($written.Length -ne $totalLength) {
    throw ("Icon write was incomplete: expected {0} bytes, found {1}." -f $totalLength, $written.Length)
}

if ($written[2] -ne 1 -or $written[4] -ne ($frames.Count -band 0xFF)) {
    throw "Icon container header is not what we wrote."
}

$firstFrameOffset = [BitConverter]::ToUInt32($written, 6 + 12)
if ($written[$firstFrameOffset] -ne 0x89 -or $written[$firstFrameOffset + 1] -ne 0x50) {
    throw "First icon frame is not PNG data."
}

Write-Host ("Icon written: {0}" -f $resolvedOutput)
Write-Host ("  sizes  : {0}" -f ($Sizes -join ', '))
Write-Host ("  bytes  : {0} (read-back verification passed)" -f $written.Length)
