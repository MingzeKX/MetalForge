<#
.SYNOPSIS
    Launches MetalForge (or attaches to a running instance) and captures the screen.

.DESCRIPTION
    Verification helper for UI work. Two capture modes:

      -Window     : capture the app's client area
      -FullScreen : capture the whole real framebuffer (default)

    Why full screen is the default here: on this machine the app is told the screen
    is 2256x1504 at 150% scaling while the actual framebuffer is smaller. Capturing
    the client area then needs DPI juggling and gets clipped. Capturing the whole
    framebuffer always shows exactly what a human sees.

    The app is left running by default so a human can look at the real UI.
    Pass -CloseAfterCapture to terminate it.

.NOTES
    ENCODING RULE: this file must stay pure ASCII. Windows PowerShell 5.1 decodes
    BOM-less .ps1 files using the system ANSI code page, which corrupts non-ASCII
    text and breaks parsing (enforced by ScriptEncodingTests).
#>
[CmdletBinding()]
param(
    [string] $ExecutablePath,

    [string] $OutputPath,

    [ValidateSet('FullScreen', 'Window')]
    [string] $Mode = 'FullScreen',

    [int] $WaitSeconds = 20,

    [int] $SettleMilliseconds = 1500,

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
    $OutputPath = Join-Path $repositoryRoot 'build\screenshots\metalforge.png'
}

# GDI+ reports a missing directory as a generic error with no hint, so prepare it early.
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath))

Add-Type -AssemblyName System.Drawing

$interopSource = @'
using System;
using System.Runtime.InteropServices;

namespace MfInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public static class NativeWindow
    {
        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }
}
'@

if (-not ('MfInterop.NativeWindow' -as [type])) {
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
        if ($candidate -ne [System.IntPtr]::Zero -and [MfInterop.NativeWindow]::IsWindowVisible($candidate)) {
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
    if ($running) {
        $running.Refresh()
        $handle = $running.MainWindowHandle
        $process = $running
    }
}

# Bring the window forward. SetForegroundWindow can silently fail under foreground
# locks, and CopyFromScreen then captures whatever is on top (this project once
# captured a browser window). Verify instead of assuming.
if ($handle -ne [System.IntPtr]::Zero) {
    if ([MfInterop.NativeWindow]::IsIconic($handle)) {
        [void][MfInterop.NativeWindow]::ShowWindow($handle, 9)  # SW_RESTORE
        Start-Sleep -Milliseconds 300
    }

    [void][MfInterop.NativeWindow]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 300

    if ([MfInterop.NativeWindow]::GetForegroundWindow() -ne $handle) {
        Write-Warning 'Target window is not in the foreground; the capture may show another window.'
    }
}

Start-Sleep -Milliseconds $SettleMilliseconds

$screenWidth = [MfInterop.NativeWindow]::GetSystemMetrics(0)   # SM_CXSCREEN
$screenHeight = [MfInterop.NativeWindow]::GetSystemMetrics(1)  # SM_CYSCREEN

if ($Mode -eq 'FullScreen') {
    $captureX = 0
    $captureY = 0
    $captureWidth = $screenWidth
    $captureHeight = $screenHeight
}
else {
    if ($handle -eq [System.IntPtr]::Zero) {
        throw 'Window mode requires a window handle.'
    }

    $clientRectangle = New-Object MfInterop.RECT
    if (-not [MfInterop.NativeWindow]::GetClientRect($handle, [ref]$clientRectangle)) {
        throw 'GetClientRect failed.'
    }

    $origin = New-Object MfInterop.POINT
    $origin.X = 0
    $origin.Y = 0
    if (-not [MfInterop.NativeWindow]::ClientToScreen($handle, [ref]$origin)) {
        throw 'ClientToScreen failed.'
    }

    $captureX = $origin.X
    $captureY = $origin.Y
    $captureWidth = $clientRectangle.Right - $clientRectangle.Left
    $captureHeight = $clientRectangle.Bottom - $clientRectangle.Top

    if ($captureX -lt 0 -or $captureY -lt 0 -or ($captureX + $captureWidth) -gt $screenWidth -or ($captureY + $captureHeight) -gt $screenHeight) {
        Write-Warning ("Client area {0},{1} {2}x{3} does not fit the framebuffer {4}x{5}; the image will be clipped." -f `
            $captureX, $captureY, $captureWidth, $captureHeight, $screenWidth, $screenHeight)
    }
}

if ($captureWidth -le 0 -or $captureHeight -le 0) {
    throw ("Capture area is empty: {0}x{1}" -f $captureWidth, $captureHeight)
}

Write-Verbose ("capturing {0},{1} {2}x{3} (mode {4})" -f $captureX, $captureY, $captureWidth, $captureHeight, $Mode)

$bitmap = New-Object System.Drawing.Bitmap($captureWidth, $captureHeight)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($captureX, $captureY, 0, 0, $bitmap.Size)
    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Host ("Screenshot saved: {0} ({1}x{2})" -f $OutputPath, $captureWidth, $captureHeight) -ForegroundColor Green

if ($CloseAfterCapture -and $process -and -not $process.HasExited) {
    $process.Kill()
    $process.WaitForExit(5000) | Out-Null
    Write-Host 'Application closed.'
}
elseif ($process -and -not $process.HasExited) {
    Write-Host ("Application left running (PID {0})." -f $process.Id) -ForegroundColor Cyan
}
