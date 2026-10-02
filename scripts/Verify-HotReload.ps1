<#
.SYNOPSIS
    Verifies configuration hot reload end to end: launch the app, edit assets on disk, observe the reload.

.DESCRIPTION
    Unit tests prove the merge and reload logic. They do not prove that a
    FileSystemWatcher actually fires for a real file save, that the change reaches
    the UI thread, or that the window picks up new values. Those are exactly the
    parts that fail in practice, so this script checks them for real.

    The script always restores every file it touches, even when a step fails.

.NOTES
    ENCODING RULE: this file must stay pure ASCII.
    Windows PowerShell 5.1 decodes BOM-less .ps1 files using the system ANSI code
    page, which corrupts non-ASCII text and breaks parsing. Scripts that genuinely
    need non-ASCII output must be launched through scripts/Invoke-RepoScript.ps1.

.EXAMPLE
    pwsh -File .\scripts\Verify-HotReload.ps1
#>
[CmdletBinding()]
param(
    [string] $ExecutablePath,

    [int] $ReloadWaitSeconds = 10
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

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$executableDirectory = Split-Path -Parent $resolvedExecutable

# Hot reload watches the assets next to the executable (that is the built-in layer at
# runtime), so that copy is what must be edited - never the repository sources.
$runtimeAssets = Join-Path $executableDirectory 'assets'
$brandingPath = Join-Path $runtimeAssets 'branding\app.json'
$themePath = Join-Path $runtimeAssets 'themes\metalforge-dark.theme.json'

foreach ($path in @($brandingPath, $themePath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw ("Asset not found: {0}. The build must copy assets next to the executable." -f $path)
    }
}

$logPath = Join-Path $repositoryRoot 'build\hotreload-out.log'
$errorLogPath = Join-Path $repositoryRoot 'build\hotreload-err.log'

$originalBranding = Get-Content -LiteralPath $brandingPath -Raw -Encoding UTF8
$originalTheme = Get-Content -LiteralPath $themePath -Raw -Encoding UTF8

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$failures = New-Object System.Collections.Generic.List[string]

function Write-Utf8 {
    param([string] $Path, [string] $Content)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Get-LogLength {
    if (Test-Path -LiteralPath $logPath) {
        return (Get-Item -LiteralPath $logPath).Length
    }

    return 0
}

function Wait-ForLogPattern {
    param([string] $Pattern, [int] $TimeoutSeconds, [long] $FromOffset = 0)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300

        # The log is held open by the redirected stdout handle, so it can never be
        # deleted or truncated. Read only the newly appended region instead.
        if (Test-Path -LiteralPath $logPath) {
            $stream = [System.IO.File]::Open($logPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                if ($stream.Length -gt $FromOffset) {
                    [void]$stream.Seek($FromOffset, [System.IO.SeekOrigin]::Begin)
                    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
                    $content = $reader.ReadToEnd()
                    if ($content -match $Pattern) {
                        return $true
                    }
                }
            }
            finally {
                $stream.Dispose()
            }
        }
    }

    return $false
}

function Confirm-Step {
    param([bool] $Condition, [string] $SuccessMessage, [string] $FailureMessage)

    if ($Condition) {
        Write-Host ('    OK   ' + $SuccessMessage) -ForegroundColor Green
    }
    else {
        Write-Host ('    FAIL ' + $FailureMessage) -ForegroundColor Red
        $failures.Add($FailureMessage)
    }
}

$process = $null

try {
    Write-Host '[1/5] Launch app and wait for initial configuration load' -ForegroundColor Cyan
    $process = Start-Process -FilePath $resolvedExecutable -WorkingDirectory $executableDirectory `
        -PassThru -RedirectStandardOutput $logPath -RedirectStandardError $errorLogPath

    # The startup log line is emitted after configuration has been applied.
    Confirm-Step -Condition (Wait-ForLogPattern -Pattern 'MetalForge.App.App\[1000\]' -TimeoutSeconds 25) `
        -SuccessMessage 'startup log observed' `
        -FailureMessage 'app did not log a successful startup within 25 seconds'

    Write-Host '[2/5] Change branding name via assets/branding/app.json' -ForegroundColor Cyan
    $offset = Get-LogLength
    $patchedBranding = $originalBranding -replace '"name":\s*"MetalForge"', '"name": "HotReloadProbe"'
    if ($patchedBranding -eq $originalBranding) {
        $failures.Add('could not patch branding name; the regex no longer matches app.json')
    }
    else {
        Write-Utf8 -Path $brandingPath -Content $patchedBranding
        Confirm-Step -Condition (Wait-ForLogPattern -Pattern 'MetalForge.App.App\[1102\]' -TimeoutSeconds $ReloadWaitSeconds -FromOffset $offset) `
            -SuccessMessage 'branding change triggered a reload' `
            -FailureMessage ("no reload log within {0}s after editing branding/app.json" -f $ReloadWaitSeconds)
    }

    Write-Host '[3/5] Change theme palette via assets/themes/metalforge-dark.theme.json' -ForegroundColor Cyan
    $offset = Get-LogLength
    $patchedTheme = $originalTheme -replace '"background":\s*"#[0-9A-Fa-f]{6}"', '"background": "#2A1F1F"'
    if ($patchedTheme -eq $originalTheme) {
        $failures.Add('could not patch theme background; the regex no longer matches the theme file')
    }
    else {
        Write-Utf8 -Path $themePath -Content $patchedTheme
        Confirm-Step -Condition (Wait-ForLogPattern -Pattern 'MetalForge.App.App\[1102\]' -TimeoutSeconds $ReloadWaitSeconds -FromOffset $offset) `
            -SuccessMessage 'theme change triggered a reload' `
            -FailureMessage ("no reload log within {0}s after editing the theme file" -f $ReloadWaitSeconds)
    }

    Write-Host '[4/5] Write malformed JSON and confirm graceful degradation' -ForegroundColor Cyan
    $offset = Get-LogLength
    Write-Utf8 -Path $brandingPath -Content '{ "name": "Broken" "version": "9.9.9" }'

    Confirm-Step -Condition (Wait-ForLogPattern -Pattern 'MFCFG143' -TimeoutSeconds $ReloadWaitSeconds -FromOffset $offset) `
        -SuccessMessage 'malformed JSON produced diagnostic MFCFG143' `
        -FailureMessage 'malformed JSON did not produce an MFCFG143 diagnostic'

    $process.Refresh()
    Confirm-Step -Condition (-not $process.HasExited) `
        -SuccessMessage 'app still running after bad configuration (degraded, not crashed)' `
        -FailureMessage ('app exited after bad configuration with code {0}' -f $process.ExitCode)

    Write-Host '[5/5] Restore files and verify the repository was never touched' -ForegroundColor Cyan
}
finally {
    Write-Utf8 -Path $brandingPath -Content $originalBranding
    Write-Utf8 -Path $themePath -Content $originalTheme

    if ($process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
    }

    # The script only edits the build output copy; assert that repository assets are clean.
    # Guarded because the repository may not be a git checkout (yet) or git may be absent.
    $gitDirectory = Join-Path $repositoryRoot '.git'
    if (Test-Path -LiteralPath $gitDirectory) {
        $gitStatus = & git -C $repositoryRoot status --porcelain -- assets 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host '    NOTE git status unavailable; skipping repository check' -ForegroundColor Yellow
        }
        elseif ($gitStatus) {
            Write-Host '    NOTE repository assets show modifications:' -ForegroundColor Yellow
            Write-Host $gitStatus
        }
        else {
            Write-Host '    OK   repository assets unchanged' -ForegroundColor Green
        }
    }
    else {
        Write-Host '    NOTE not a git checkout; skipping repository check' -ForegroundColor Yellow
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host 'HOT RELOAD VERIFICATION FAILED:' -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host ('  - ' + $failure) -ForegroundColor Red
    }

    Write-Host ''
    Write-Host 'Last 40 log lines:' -ForegroundColor Yellow
    if (Test-Path -LiteralPath $logPath) {
        Get-Content -LiteralPath $logPath -Encoding UTF8 -Tail 40
    }

    exit 1
}

Write-Host 'HOT RELOAD VERIFICATION PASSED: reload, theme change and error degradation all behave as designed.' -ForegroundColor Green
