<#
.SYNOPSIS
    Runs the MetalForge test suite.

.DESCRIPTION
    Runs the xUnit v3 test assembly directly.

    WHY NOT "dotnet test":
    The test projects use xunit.v3.mtp-v2 (Microsoft.Testing.Platform). On .NET 10
    SDK 10.0.401 combined with xunit.v3 4.0.1, "dotnet test" reports
    "ran zero tests" with exit code 5 even though the same assembly runs all tests
    correctly when executed directly. Until that combination works, this script is
    the supported entry point; see docs/manual-verification/M0.md.

    The assembly is a self-contained test runner: run it with no arguments to
    execute everything, or pass platform options such as -filter or -list-tests.

.EXAMPLE
    .\scripts\Test.ps1
    .\scripts\Test.ps1 -Configuration Release
    .\scripts\Test.ps1 -TestArguments -filter "FullyQualifiedName~HexColor"
    .\scripts\Test.ps1 -ListTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoBuild,

    [switch] $ListTests,

    # Forwarded verbatim to the test runner, e.g. -TestArguments -filter "x"
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $TestArguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repositoryRoot 'tests\MetalForge.Core.Tests\MetalForge.Core.Tests.csproj'

if (-not (Test-Path -LiteralPath $testProject)) {
    throw ("Test project not found: {0}" -f $testProject)
}

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Assembly name is fixed by the test csproj; locate it under build/<Configuration>/.
$assembly = Get-ChildItem -Path (Join-Path $repositoryRoot "build\$Configuration\MetalForge.Core.Tests") -Filter 'MetalForge.Core.Tests.dll' -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $assembly) {
    throw ("Test assembly not built. Expected under build\{0}\MetalForge.Core.Tests" -f $Configuration)
}

Push-Location $repositoryRoot
try {
    $runnerArguments = @()
    if ($ListTests) { $runnerArguments += '-list-tests' }
    if ($TestArguments.Count -gt 0) { $runnerArguments += $TestArguments }

    Write-Host ("Running tests: {0}" -f $assembly.FullName) -ForegroundColor Cyan
    & dotnet $assembly.FullName @runnerArguments
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host "All tests passed." -ForegroundColor Green
    }
    else {
        Write-Host ("Tests FAILED with exit code {0}" -f $exitCode) -ForegroundColor Red
    }

    exit $exitCode
}
finally {
    Pop-Location
}
