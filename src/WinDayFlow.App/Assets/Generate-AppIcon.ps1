[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing.Common
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinDayFlowIconNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr handle);
}
'@

$assetRoot = $PSScriptRoot
$pngPath = Join-Path $assetRoot 'WinDayFlow.png'
$icoPath = Join-Path $assetRoot 'WinDayFlow.ico'
$bitmap = [System.Drawing.Bitmap]::new(64, 64)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$backgroundPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
$panelPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
$iconHandle = [IntPtr]::Zero

try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $backgroundPath.AddArc(0, 0, 24, 24, 180, 90)
    $backgroundPath.AddArc(40, 0, 24, 24, 270, 90)
    $backgroundPath.AddArc(40, 40, 24, 24, 0, 90)
    $backgroundPath.AddArc(0, 40, 24, 24, 90, 90)
    $backgroundPath.CloseFigure()
    $graphics.FillPath(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(32, 35, 38)),
        $backgroundPath)

    $panelPath.AddArc(11, 10, 12, 12, 180, 90)
    $panelPath.AddArc(41, 10, 12, 12, 270, 90)
    $panelPath.AddArc(41, 42, 12, 12, 0, 90)
    $panelPath.AddArc(11, 42, 12, 12, 90, 90)
    $panelPath.CloseFigure()
    $graphics.FillPath(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(247, 248, 250)),
        $panelPath)
    $graphics.FillRectangle(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(32, 35, 38)),
        11,
        19,
        42,
        4)

    $flowPen = [System.Drawing.Pen]::new(
        [System.Drawing.Color]::FromArgb(230, 95, 75),
        5)
    $flowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $flowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $flowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    try {
        $graphics.DrawLines(
            $flowPen,
            [System.Drawing.Point[]]@(
                [System.Drawing.Point]::new(18, 39),
                [System.Drawing.Point]::new(27, 30),
                [System.Drawing.Point]::new(36, 39),
                [System.Drawing.Point]::new(46, 25)))
    }
    finally {
        $flowPen.Dispose()
    }
    $graphics.FillEllipse(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(45, 157, 120)),
        42,
        21,
        8,
        8)

    $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $iconHandle = $bitmap.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($iconHandle)
    try {
        $stream = [System.IO.FileStream]::new(
            $icoPath,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $icon.Save($stream)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $icon.Dispose()
    }
}
finally {
    if ($iconHandle -ne [IntPtr]::Zero) {
        [void][WinDayFlowIconNative]::DestroyIcon($iconHandle)
    }
    $panelPath.Dispose()
    $backgroundPath.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
