<#
.SYNOPSIS
    Launches MetalForge, waits for the main window, captures the client area, then exits.

.DESCRIPTION
    Manual visual verification helper.

    "It compiled" is not evidence that the UI renders. A mis-wired theme resource
    shows up as a blank window rather than as an exception, and an oversized window
    silently hides whole panels off-screen. This script makes the check
    reproducible: start the app, grab the window client area, save a PNG, exit.

    The launched process is always terminated, even when capture fails.

    Implementation notes (each one cost real debugging time):
      - Capture the CLIENT area via GetClientRect + ClientToScreen. Feeding
        GetWindowRect's physical pixels to CopyFromScreen produced shifted, cropped
        images on a 150%-scaled display.
      - P/Invoke parameters declared `ref` require a pre-created struct instance;
        only `out` parameters create the variable automatically.
      - GDI+ reports a missing output directory as a generic "A generic error
        occurred in GDI+", so the directory is created up front.

.PARAMETER ExecutablePath
    Path to MetalForge.exe. Defaults to the Debug build output.

.PARAMETER OutputPath
    Where to write the PNG. Defaults to build/screenshots/.

.PARAMETER WaitSeconds
    How long to wait for the main window to appear.

.PARAMETER SettleMilliseconds
    Extra delay after the window appears, to let one render pass complete.

.EXAMPLE
    pwsh -File .\scripts\Capture-AppWindow.ps1
#>
[CmdletBinding()]
param(
    [string] $ExecutablePath,

    [string] $OutputPath,

    [int] $WaitSeconds = 20,

    [int] $SettleMilliseconds = 1500,

    [switch] $KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path $repositoryRoot 'build\Debug\MetalForge.App\bin\Debug\net10.0\MetalForge.exe'
}

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw ("Executable not found: {0}. Build first: .\scripts\Build.ps1" -f $ExecutablePath)
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repositoryRoot 'build\screenshots\metalforge.png'
}

# GDI+ reports a missing directory as an unhelpful generic error; prepare it first.
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
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }
}
'@

if (-not ('MfInterop.NativeWindow' -as [type])) {
    Add-Type -TypeDefinition $interopSource -Language CSharp
}

$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$workingDirectory = Split-Path -Parent $executable

Write-Host ("Launching {0}" -f $executable) -ForegroundColor Cyan

$process = Start-Process -FilePath $executable -WorkingDirectory $workingDirectory -PassThru
$captured = $false

try {
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $handle = [System.IntPtr]::Zero

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

    [void][MfInterop.NativeWindow]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds $SettleMilliseconds

    $clientRectangle = New-Object MfInterop.RECT
    if (-not [MfInterop.NativeWindow]::GetClientRect($handle, [ref]$clientRectangle)) {
        throw 'GetClientRect failed.'
    }

    # `ref` parameters need an existing instance; only `out` parameters auto-create.
    $origin = New-Object MfInterop.POINT
    $origin.X = 0
    $origin.Y = 0
    if (-not [MfInterop.NativeWindow]::ClientToScreen($handle, [ref]$origin)) {
        throw 'ClientToScreen failed.'
    }

    $width = $clientRectangle.Right - $clientRectangle.Left
    $height = $clientRectangle.Bottom - $clientRectangle.Top
    $left = $origin.X
    $top = $origin.Y

    if ($width -le 0 -or $height -le 0) {
        throw ("Client area is empty: {0}x{1}" -f $width, $height)
    }

    # 用真实帧缓冲尺寸做最后一道钳制。
    # 本机实测：应用报告的屏幕是 2256x1504（150% 缩放），而当前会话的实际帧缓冲
    # 只有 1280x720（远程/虚拟显示常见）。两者不一致时，窗口本来就会有一部分在
    # 真实帧缓冲之外，CopyFromScreen 会尝试抓取不存在的像素 —— 结果是图片被
    # 静默裁切，看起来像"界面少了一块"。这里把抓取矩形裁剪到真实屏幕范围内。
    $screenWidth = [MfInterop.NativeWindow]::GetSystemMetrics(0)   # SM_CXSCREEN
    $screenHeight = [MfInterop.NativeWindow]::GetSystemMetrics(1)  # SM_CYSCREEN

    if ($screenWidth -gt 0 -and $screenHeight -gt 0) {
        $clippedRight = [Math]::Min($left + $width, $screenWidth)
        $clippedBottom = [Math]::Min($top + $height, $screenHeight)
        $left = [Math]::Max($left, 0)
        $top = [Math]::Max($top, 0)
        $width = $clippedRight - $left
        $height = $clippedBottom - $top

        if ($width -le 0 -or $height -le 0) {
            throw ("Window lies entirely outside the real framebuffer ({0}x{1})." -f $screenWidth, $screenHeight)
        }

        Write-Verbose ("framebuffer {0}x{1}; capturing {2},{3} {4}x{5}" -f $screenWidth, $screenHeight, $left, $top, $width, $height)
    }

    $dpi = [uint32]96
    try {
        $dpi = [MfInterop.NativeWindow]::GetDpiForWindow($handle)
    }
    catch [System.Management.Automation.MethodInvocationException] {
        Write-Verbose 'GetDpiForWindow unavailable; assuming 96 DPI.'
    }

    if ($dpi -lt 48) { $dpi = 96 }

    Write-Verbose ("client origin {0},{1} size {2}x{3}, dpi {4}" -f $left, $top, $width, $height, $dpi)

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $captured = $true
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    Write-Host ("Screenshot saved: {0} ({1}x{2})" -f $OutputPath, $width, $height) -ForegroundColor Green
}
finally {
    if (-not $KeepOpen -and -not $process.HasExited) {
        # Windows PowerShell 5.1 的 Process 对象来自 .NET Framework，
        # 没有 Kill(bool entireProcessTree) 重载，只能调用无参 Kill()。
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
        Write-Host 'Application closed.'
    }
}

if (-not $captured) {
    exit 1
}
