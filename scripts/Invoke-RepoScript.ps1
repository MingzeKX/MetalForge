<#
.SYNOPSIS
    Bootstraps a repository script under PowerShell 7 (pwsh).

.DESCRIPTION
    Why this exists:
    Windows PowerShell 5.1 decodes BOM-less .ps1 files using the system ANSI code
    page (GBK on Chinese Windows). Any script containing non-ASCII text - which is
    most of ours, since comments and messages are Chinese by default - therefore
    becomes mojibake and fails to parse. Tools that write UTF-8 without a BOM make
    this trivially easy to hit.

    Instead of relying on everyone remembering to keep files ASCII-only or to
    preserve a BOM, run scripts through this bootstrap. It re-launches the target
    script under pwsh (which always reads UTF-8) and fails with an actionable
    message if pwsh is unavailable.

.EXAMPLE
    .\scripts\Invoke-RepoScript.ps1 -Path .\scripts\Build-Tools.ps1 -Arguments -Verify
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [string[]] $Arguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    throw ("Script not found: {0}" -f $Path)
}

$resolvedScript = (Resolve-Path -LiteralPath $Path).Path

$pwshCommand = Get-Command pwsh -ErrorAction SilentlyContinue
if (-not $pwshCommand) {
    throw @"
PowerShell 7 (pwsh) was not found on PATH.

This script needs pwsh because Windows PowerShell 5.1 mis-decodes UTF-8 scripts
that have no byte-order mark, corrupting non-ASCII text.

Install PowerShell 7:
    winget install --id Microsoft.PowerShell --source winget

Then re-run:
    pwsh -File "$resolvedScript"
"@
}

Write-Verbose ("Launching {0} under {1}" -f $resolvedScript, $pwshCommand.Source)

& $pwshCommand.Source -NoLogo -NoProfile -File $resolvedScript @Arguments
exit $LASTEXITCODE
