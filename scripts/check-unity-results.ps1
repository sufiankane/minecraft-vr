#requires -Version 5.1
<#
.SYNOPSIS
    Assert that a Unity NUnit XML results file proves the requested suite ran.

.DESCRIPTION
    Shared release-gate check for the hosted and self-hosted release routes (and
    usable by any lane that runs `unity test --output <results.xml>`). It fails
    loudly instead of trusting `unity test`'s exit code alone:

      - the results file must exist (a missing file means the suite did not run);
      - it must contain a /test-run element;
      - total must be greater than zero (zero tests is not a pass);
      - failed must be zero;
      - skipped must be zero (an ignored test is not a pass);
      - every required assembly must appear as a test-suite of type Assembly.

    A non-zero `unity test` exit code is also a failure when passed via
    -TestExitCode, but the assertions above are the point: they catch an
    invocation that exits 0 while running nothing, or while skipping tests.

.PARAMETER Mode
    Unity test mode the results belong to (e.g. EditMode or PlayMode), used in
    messages.

.PARAMETER ResultsPath
    Path to the NUnit XML file produced by `unity test --output`.

.PARAMETER RequiredAssemblies
    Test assembly names that must be present in the results (one or more).

.PARAMETER TestExitCode
    Exit code reported by `unity test` for this run (default 0).

.EXAMPLE
    & ./scripts/check-unity-results.ps1 -Mode EditMode -ResultsPath $env:TEMP/cg-unity-editmode-results.xml -RequiredAssemblies Cubeglass.Unity.Bridge.Tests.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Mode,
    [Parameter(Mandatory = $true)][string]$ResultsPath,
    [Parameter(Mandatory = $true)][string[]]$RequiredAssemblies,
    [int]$TestExitCode = 0
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ResultsPath)) {
    Write-Host "::error::$Mode tests produced no NUnit results at $ResultsPath (exit $TestExitCode); the suite did not run."
    exit 1
}
[xml]$report = Get-Content -LiteralPath $ResultsPath -Raw
$run = $report.SelectSingleNode('/test-run')
if ($null -eq $run) {
    Write-Host "::error::$Mode results at $ResultsPath contain no /test-run element."
    exit 1
}
$total = [int]$run.total
$failed = [int]$run.failed
$skipped = [int]$run.skipped
$assemblies = @($report.SelectNodes('.//test-suite[@type="Assembly"]') | ForEach-Object { $_.name })
$missing = @($RequiredAssemblies | Where-Object { $assemblies -notcontains $_ })
if ($TestExitCode -ne 0 -or $total -le 0 -or $failed -gt 0 -or $skipped -gt 0 -or $missing.Count -gt 0) {
    Write-Host "::error::$Mode tests not clean: exit=$TestExitCode total=$total failed=$failed skipped=$skipped missingAssemblies=$($missing -join ',')."
    exit 1
}
Write-Host "$Mode tests passed: $total test(s)."
exit 0
