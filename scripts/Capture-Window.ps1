<#
.SYNOPSIS
    Captures a window's own rendering into a PNG, regardless of what is on top of it.

.DESCRIPTION
    Uses PrintWindow with PW_RENDERFULLCONTENT so the window is asked to draw itself
    into a bitmap. This avoids the two problems that made screen-copy captures
    useless for UI review on this machine:

      1) CopyFromScreen only sees the session framebuffer (1504x1003 here) while the
         desktop is 2254x1503, so captures came out as a fraction of the screen;
      2) whatever window happens to be on top ends up in the image instead.

    PrintWindow works even when the target is occluded or not focused.

.NOTES
    ENCODING RULE: this file must stay pure ASCII (see ScriptEncodingTests).
#>
[CmdletBinding()]
param(
    [string] $ExecutablePath,

    [string] $OutputPath,

    [int] $WaitSeconds = 20,

    [int] $SettleMilliseconds = 1800,

    [switch] $NoLaunch,

    [switch] $CloseAfterCapture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path $repositoryRoot 'build\Debug\MetalForge.App\bin\Debug\net10.0\MetalForge.exe'
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repositoryRoot 'build\screenshots\window.png'
}

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath))

Add-Type -AssemblyName System.Drawing

$interopSource = @'
using System;
using System.Runtime.InteropServices;

namespace MfPrint
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static class WindowPrinter
    {
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }
}
'@

if (-not ('MfPrint.WindowPrinter' -as [type])) {
    Add-Type -TypeDefinition $interopSource -Language CSharp
}

$process = $null
$handle = [System.IntPtr]::Zero

if (-not $NoLaunch) {
    if (-not (Test-Path -LiteralPath $ExecutablePath)) {
        throw ("Executable not found: {0}. Build first: .\scripts\Build.ps1" -f $ExecutablePath)
    }

    $executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
    Write-Host ("Launching {0}" -f $executable) -ForegroundColor Cyan

    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250

        if ($process.HasExited) {
            throw ("Application exited before a window appeared (exit code {0})." -f $process.ExitCode)
        }

        $process.Refresh()
        $candidate = $process.MainWindowHandle
        if ($candidate -ne [System.IntPtr]::Zero -and [MfPrint.WindowPrinter]::IsWindowVisible($candidate)) {
            $handle = $candidate
            break
        }
    }

    if ($handle -eq [System.IntPtr]::Zero) {
        throw ("No visible main window after {0} seconds." -f $WaitSeconds)
    }
}
else {
    $running = Get-Process -Name 'MetalForge' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $running) {
        throw 'No running MetalForge process found; drop -NoLaunch to start one.'
    }

    $running.Refresh()
    $handle = $running.MainWindowHandle
    $process = $running
}

if ([MfPrint.WindowPrinter]::IsIconic($handle)) {
    [void][MfPrint.WindowPrinter]::ShowWindow($handle, 9)  # SW_RESTORE
    Start-Sleep -Milliseconds 500
}

Start-Sleep -Milliseconds $SettleMilliseconds

$rectangle = New-Object MfPrint.RECT
if (-not [MfPrint.WindowPrinter]::GetWindowRect($handle, [ref]$rectangle)) {
    throw 'GetWindowRect failed.'
}

$width = $rectangle.Right - $rectangle.Left
$height = $rectangle.Bottom - $rectangle.Top

if ($width -le 0 -or $height -le 0) {
    throw ("Window rectangle is empty: {0}x{1}" -f $width, $height)
}

Write-Verbose ("window rect ({0},{1}) {2}x{3}; desktop {4}x{5}" -f `
    $rectangle.Left, $rectangle.Top, $width, $height, `
    [MfPrint.WindowPrinter]::GetSystemMetrics(0), [MfPrint.WindowPrinter]::GetSystemMetrics(1))

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$targetHdc = $graphics.GetHdc()
$printed = $false
try {
    # PW_RENDERFULLCONTENT (0x00000002) is what makes DirectComposition / GPU-rendered
    # windows (Avalonia uses one) actually draw into the bitmap; without it the result
    # is often an empty white image.
    $printed = [MfPrint.WindowPrinter]::PrintWindow($handle, $targetHdc, 2)
}
finally {
    $graphics.ReleaseHdc($targetHdc)
    $graphics.Dispose()
}

if (-not $printed) {
    $bitmap.Dispose()
    throw 'PrintWindow failed; the window may not support WM_PRINT.'
}

$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Host ("Window captured: {0} ({1}x{2})" -f $OutputPath, $width, $height) -ForegroundColor Green

if ($CloseAfterCapture -and $process -and -not $process.HasExited) {
    $process.Kill()
    $process.WaitForExit(5000) | Out-Null
    Write-Host 'Application closed.'
}
elseif ($process -and -not $process.HasExited) {
    Write-Host ("Application left running (PID {0})." -f $process.Id) -ForegroundColor Cyan
}
