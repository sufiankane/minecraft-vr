#requires -Version 5.1
<#
.SYNOPSIS
    Enter the MSVC x64 developer shell used by Cubeglass C++ builds.

.DESCRIPTION
    Locates the newest Visual Studio installation that provides the C++ x64
    toolset, imports Microsoft.VisualStudio.DevShell.dll and enters the x64
    developer shell so cl.exe, ninja and cmake can be used from this session.

    Dot-source it so the environment changes stay in your shell:

        . scripts/dev-shell.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'

$vcpkgRootWasSet = -not [string]::IsNullOrWhiteSpace($env:VCPKG_ROOT)

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found at '$vswhere'. Install Visual Studio Build Tools with the 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' component."
}

$installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>&1
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$installPath")) {
    throw "No Visual Studio installation with component 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' was found. Install VS Build Tools 18 with the C++ x64 toolset."
}
$installPath = "$installPath".Trim()

$devShellDll = Join-Path $installPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
if (-not (Test-Path $devShellDll)) {
    throw "Microsoft.VisualStudio.DevShell.dll not found at '$devShellDll'. The Visual Studio installation looks incomplete."
}

Import-Module $devShellDll -ErrorAction Stop

$devCmdArguments = "-arch=$Arch -host_arch=x64"
Enter-VsDevShell -VsInstallPath $installPath -SkipAutomaticLocation -DevCmdArguments $devCmdArguments -ErrorAction Stop

if (-not $vcpkgRootWasSet) {
    $env:VCPKG_ROOT = Join-Path $env:USERPROFILE 'vcpkg'
}

Write-Output "Entered MSVC $Arch developer shell: $installPath"
Write-Output "VCPKG_ROOT: $env:VCPKG_ROOT"
