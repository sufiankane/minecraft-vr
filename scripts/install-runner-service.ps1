#requires -Version 5.1
<#
.SYNOPSIS
    Install the existing C:\actions-runner checkout as a Windows service.

.DESCRIPTION
    TD-047 runbook helper. The repository's self-hosted release route needs the
    runner to survive logout and restarts, and it must run as the user that owns
    the Unity Hub activation (%LOCALAPPDATA%\Unity\licenses is per-user). This
    script requires an elevated prompt, checks the runner is already configured,
    installs the service with the runner's own svc.cmd, starts it and prints its
    account and status.

    The script is idempotent: an already-installed service is reported and left
    alone. It never downloads or configures the runner; it only service-ifies an
    existing checkout. The actual install remains an owner action.

.PARAMETER RunnerDirectory
    The configured runner directory; defaults to C:\actions-runner.

.PARAMETER SkipStart
    Install but do not start the service.

.EXAMPLE
    # From an elevated PowerShell prompt:
    ./scripts/install-runner-service.ps1
#>
[CmdletBinding()]
param(
    [string]$RunnerDirectory = 'C:\actions-runner',
    [switch]$SkipStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '::error::This script needs an elevated PowerShell prompt (Run as Administrator): installing a Windows service requires it.'
    exit 1
}

$svc = Join-Path $RunnerDirectory 'svc.cmd'
$config = Join-Path $RunnerDirectory '.runner'
if (-not (Test-Path -LiteralPath $svc)) {
    Write-Host "::error::$svc not found. Point -RunnerDirectory at the configured runner checkout (the default is C:\actions-runner)."
    exit 1
}
if (-not (Test-Path -LiteralPath $config)) {
    Write-Host "::error::$RunnerDirectory is not a configured runner (no .runner file). Run config.cmd in that directory first, as documented in .github/workflows/release.yml."
    exit 1
}

$serviceFile = Join-Path $RunnerDirectory '.service'
$serviceName = if (Test-Path -LiteralPath $serviceFile) {
    (Get-Content -LiteralPath $serviceFile -Raw).Trim()
}
else {
    ''
}

if ($serviceName -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
    Write-Host "The runner service '$serviceName' is already installed:"
    Get-Service -Name $serviceName | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host 'Nothing to do. To reinstall, stop it and run `svc.cmd uninstall` in the runner directory first.'
    exit 0
}

Write-Host "Installing the runner service from $RunnerDirectory ..."
& $svc install
if ($LASTEXITCODE -ne 0) {
    Write-Host "::error::svc.cmd install failed with exit code $LASTEXITCODE."
    exit 1
}

if (-not $serviceName) {
    $serviceName = (Get-Content -LiteralPath $serviceFile -Raw).Trim()
}

if (-not $SkipStart) {
    & $svc start
    if ($LASTEXITCODE -ne 0) {
        Write-Host "::error::svc.cmd start failed with exit code $LASTEXITCODE. Inspect the service and the runner's _diag logs before re-running."
        exit 1
    }
}

Write-Host ''
Write-Host "Service '$serviceName' configured. The runner must run as the Windows user that activated Unity through the Hub"
Write-Host "(the licence lives in %LOCALAPPDATA%\Unity\licenses). Check the account with:"
Write-Host "  sc.exe qc `"$serviceName`""
Write-Host 'If the account is wrong, stop the service and reconfigure it, then start it again:'
Write-Host "  sc.exe config `"$serviceName`" obj= `"<domain-or-machine>\<user>`" password= `"<password>`""
Write-Host "  Restart-Service '$serviceName'"
Write-Host 'The full runbook is in docs/ci.md ("Self-hosted runner service").'
Get-Service -Name $serviceName | Format-Table Status, Name, StartType -AutoSize | Out-String | Write-Host
exit 0
