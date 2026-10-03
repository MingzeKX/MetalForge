#!/usr/bin/env node
/*
 * capture-window.mjs - capture a Windows app window into a PNG, without occlusion.
 *
 * Why this exists and why it replaces the PowerShell version:
 *
 *   - CopyFromScreen in a DPI-unaware process captures the wrong region on scaled
 *     displays (this machine runs at 150%): the image came out shifted and cropped.
 *   - PrintWindow with PW_RENDERFULLCONTENT returns stale or blank content for
 *     GPU-composited windows (Avalonia uses one), so the capture showed whatever
 *     was behind the window instead of the window itself.
 *
 * The reliable approach used here:
 *   1. declare per-monitor-v2 DPI awareness so screen pixels and API coordinates
 *      are in the same space;
 *   2. move the window to (0,0) and shrink it to fit the real screen, so nothing
 *      can occlude it;
 *   3. grab that exact rectangle with .NET's Graphics.CopyFromScreen;
 *   4. restore the original position/size.
 *
 * Usage:
 *   node scripts/capture-window.mjs [--out build/screenshots/window.png]
 *                                   [--attach] [--keep-alive] [--wait 20]
 *
 * --attach   use an already running MetalForge process instead of launching one
 * --keep-alive  leave the app running when done (default: leave it running too,
 *               unless --close is passed)
 */

import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import process from 'node:process';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, '..');

/* ------------------------------------------------------------------ */
/* arguments                                                           */
/* ------------------------------------------------------------------ */

function parseArguments(argv) {
  const options = {
    out: resolve(repositoryRoot, 'build/screenshots/window.png'),
    attach: false,
    close: false,
    processId: null,
    waitSeconds: 20,
    settleMilliseconds: 1200,
    executable: resolve(repositoryRoot, 'build/Debug/MetalForge.App/bin/Debug/net10.0/MetalForge.exe'),
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    switch (argument) {
      case '--out': options.out = resolve(argv[++index]); break;
      case '--attach': options.attach = true; break;
      case '--close': options.close = true; break;
      case '--pid': options.processId = Number(argv[++index]); break;
      case '--wait': options.waitSeconds = Number(argv[++index]); break;
      case '--exe': options.executable = resolve(argv[++index]); break;
      default: throw new Error(`Unknown argument: ${argument}`);
    }
  }

  return options;
}

/* ------------------------------------------------------------------ */
/* PowerShell bridge                                                   */
/*                                                                     */
/* Node has no built-in way to call Win32; PowerShell already carries  */
/* the interop we need, so it is used here purely as a thin FFI.       */
/* The heavy lifting (DPI awareness, capture, image encoding) happens  */
/* in that same process so coordinates stay in one space.              */
/* ------------------------------------------------------------------ */

function runPowerShell(script) {
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8',
    windowsHide: true,
  });

  if (result.error) {
    throw result.error;
  }

  const output = `${result.stdout ?? ''}${result.stderr ?? ''}`.trim();
  if (result.status !== 0) {
    throw new Error(`PowerShell failed (exit ${result.status}):\n${output}`);
  }

  return output;
}

/** Brings the process to the foreground and waits for its main window. */
function waitForWindow(processId, waitSeconds) {
  const script = `
$ErrorActionPreference = 'Stop'
$deadline = (Get-Date).AddSeconds(${waitSeconds})
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 250
  $p = Get-Process -Id ${processId} -ErrorAction SilentlyContinue
  if (-not $p) { throw 'Process exited before a window appeared.' }
  $p.Refresh()
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $p.MainWindowHandle.ToInt64(); break }
}
`;
  return Number(runPowerShell(script));
}

/** Finds an already running MetalForge window. */
function findRunningWindow() {
  const output = runPowerShell(`
$ErrorActionPreference = 'Stop'
$p = Get-Process -Name 'MetalForge' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
if (-not $p) { '' } else { "$($p.Id)|$($p.MainWindowHandle.ToInt64())" }
`);

  if (!output) {
    return null;
  }

  const [processId, handle] = output.split('|');
  return { processId: Number(processId), handle: Number(handle) };
}

/* ------------------------------------------------------------------ */
/* capture                                                             */
/* ------------------------------------------------------------------ */

/**
 * Moves the window to (0,0), resizes it to the real work area, captures it, restores it.
 * Everything happens inside one PowerShell process so that DPI awareness and capture
 * share the same coordinate space.
 */
function capture(handle, outputPath, settleMilliseconds) {
  mkdirSync(dirname(outputPath), { recursive: true });

  const script = `
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Declare per-monitor-v2 DPI awareness BEFORE any GDI use.
# Without it the process is DPI-unaware, Windows virtualises coordinates, and the
# captured region does not match the rectangle we asked for (the bug that produced
# shifted, cropped screenshots on this 150% display).
Add-Type -Namespace MfShot -Name Dpi -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();
'@
try { [void][MfShot.Dpi]::SetProcessDpiAwarenessContext([IntPtr](-4)) } catch { }
try { [void][MfShot.Dpi]::SetProcessDPIAware() } catch { }

Add-Type -Namespace MfShot -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool MoveWindow(System.IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern int GetSystemMetrics(int nIndex);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

$handle = [IntPtr]${handle}

# Restore if minimised, then move to the top-left corner and fit inside the screen.
# Positioning at (0,0) is what removes occlusion entirely: nothing can be on top of it.
[void][MfShot.Win]::ShowWindow($handle, 9)
Start-Sleep -Milliseconds 300
[void][MfShot.Win]::SetForegroundWindow($handle)

$original = New-Object MfShot.Win+RECT
[void][MfShot.Win]::GetWindowRect($handle, [ref]$original)

$screenWidth = [MfShot.Win]::GetSystemMetrics(0)
$screenHeight = [MfShot.Win]::GetSystemMetrics(1)

$targetWidth = [Math]::Min($original.Right - $original.Left, $screenWidth)
$targetHeight = [Math]::Min($original.Bottom - $original.Top, $screenHeight - 40)

[void][MfShot.Win]::MoveWindow($handle, 0, 0, $targetWidth, $targetHeight, $true)
Start-Sleep -Milliseconds ${settleMilliseconds}

$rectangle = New-Object MfShot.Win+RECT
[void][MfShot.Win]::GetWindowRect($handle, [ref]$rectangle)

$width = $rectangle.Right - $rectangle.Left
$height = $rectangle.Bottom - $rectangle.Top
if ($width -le 0 -or $height -le 0) { throw "Empty window rectangle: $width x $height" }

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
  $graphics.CopyFromScreen($rectangle.Left, $rectangle.Top, 0, 0, $bitmap.Size)
  $bitmap.Save('${outputPath.replace(/\\/g, '\\\\')}', [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
  $graphics.Dispose()
  $bitmap.Dispose()

  # Always restore the window, even if the capture failed.
  [void][MfShot.Win]::MoveWindow($handle, $original.Left, $original.Top,
    $original.Right - $original.Left, $original.Bottom - $original.Top, $true)
}

"CAPTURED $width x $height"
`;

  return runPowerShell(script);
}

/* ------------------------------------------------------------------ */
/* main                                                                */
/* ------------------------------------------------------------------ */

const options = parseArguments(process.argv.slice(2));
let launched = null;

try {
  let handle;
  let processId;

  if (options.processId !== null) {
    // Attach to an explicit process id.
    //
    // Needed because FindRunningWindow relies on MainWindowHandle, which is zero for
    // windows with ShowInTaskbar=false (our splash window, for example). Those windows
    // are still perfectly capturable if we are told which process owns them.
    processId = options.processId;
    handle = waitForWindow(processId, options.waitSeconds);
    if (!handle) {
      throw new Error(`Process ${processId} has no capturable window.`);
    }
    console.log(`Attached to PID ${processId}`);
  } else if (options.attach) {
    const running = findRunningWindow();
    if (!running) {
      throw new Error('No running MetalForge window found. Use --pid, or drop --attach to launch one.');
    }
    ({ processId, handle } = running);
    console.log(`Attached to MetalForge (PID ${processId})`);
  } else {
    console.log(`Launching ${options.executable}`);
    launched = spawn(options.executable, [], { detached: true, stdio: 'ignore' });
    launched.unref();
    processId = launched.pid;

    handle = waitForWindow(processId, options.waitSeconds);
    if (!handle) {
      throw new Error(`No window appeared within ${options.waitSeconds}s.`);
    }
  }

  const result = capture(handle, options.out, options.settleMilliseconds);
  console.log(`Screenshot saved: ${options.out} (${result.replace('CAPTURED ', '')})`);

  if (options.close && launched) {
    spawnSync('powershell.exe', ['-NoProfile', '-Command', `Stop-Process -Id ${processId} -Force -ErrorAction SilentlyContinue`], { windowsHide: true });
    console.log('Application closed.');
  } else {
    console.log(`Application left running (PID ${processId}).`);
  }
} catch (error) {
  console.error(`capture failed: ${error.message}`);
  process.exitCode = 1;
}
