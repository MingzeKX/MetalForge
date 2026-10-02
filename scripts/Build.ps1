<#
.SYNOPSIS
    Builds the MetalForge solution.

.DESCRIPTION
    Wrapper around dotnet build that keeps the source tree clean and produces a
    compact summary. All build output goes to build/<Configuration>/ via
    Directory.Build.props.

.EXAMPLE
    .\scripts\Build.ps1
    .\scripts\Build.ps1 -Configuration Release -NoRestore
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoRestore,

    # The .NET 10 SDK generates an XML-format .slnx by default for "dotnet new sln".
    [string] $Solution = 'MetalForge.slnx'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot $Solution

if (-not (Test-Path -LiteralPath $solutionPath)) {
    throw ("Solution not found: {0}" -f $solutionPath)
}

Push-Location $repositoryRoot
try {
    $arguments = @('build', $solutionPath, '-c', $Configuration, '--nologo', '-v', 'minimal')
    if ($NoRestore) { $arguments += '--no-restore' }

    Write-Host ("Building {0} [{1}]" -f $Solution, $Configuration) -ForegroundColor Cyan
    & dotnet @arguments
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host ("Build succeeded: {0}" -f $Configuration) -ForegroundColor Green
    }
    else {
        Write-Host ("Build FAILED with exit code {0}" -f $exitCode) -ForegroundColor Red
    }

    exit $exitCode
}
finally {
    Pop-Location
}
