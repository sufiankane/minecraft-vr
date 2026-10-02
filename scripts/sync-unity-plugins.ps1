#requires -Version 5.1
<#
.SYNOPSIS
    Build and copy the managed Unity plugins into the Cubeglass project.

.DESCRIPTION
    Unity cannot resolve an assembly-definition reference to Cubeglass.CoreMath
    because CoreMath is a plain .NET library outside the Unity project. Instead,
    the bridge package loads it as a managed plugin from
    `unity/Cubeglass/Assets/Plugins/managed`, which Unity references
    automatically. This script:

      1. builds `dotnet/src/CoreMath` in Release (netstandard2.1);
      2. copies the produced `Cubeglass.CoreMath.dll` to
         `unity/Cubeglass/Assets/Plugins/managed/Cubeglass.CoreMath.dll`.

    It fails loudly when the build fails or the expected DLL is missing, the
    same way `scripts/ci-local.ps1` fails loudly for the native bridge DLL.
    The copied DLL (and its generated .meta) is git-ignored.

.EXAMPLE
    powershell -File scripts/sync-unity-plugins.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CoreMathProject = Join-Path $RepoRoot 'dotnet\src\CoreMath\Cubeglass.CoreMath.csproj'
$CoreMathDll = Join-Path $RepoRoot 'dotnet\src\CoreMath\bin\Release\netstandard2.1\Cubeglass.CoreMath.dll'
$ManagedPluginsDir = Join-Path $RepoRoot 'unity\Cubeglass\Assets\Plugins\managed'

Write-Host ">>> dotnet build '$CoreMathProject' --configuration Release" -ForegroundColor Cyan
& dotnet build $CoreMathProject --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed (exit $LASTEXITCODE): $CoreMathProject"
}

if (-not (Test-Path -LiteralPath $CoreMathDll)) {
    throw "managed plugin not found at '$CoreMathDll' after the Release build; check the CoreMath target framework and output path"
}

New-Item -ItemType Directory -Path $ManagedPluginsDir -Force | Out-Null
Copy-Item -LiteralPath $CoreMathDll -Destination (Join-Path $ManagedPluginsDir 'Cubeglass.CoreMath.dll') -Force
Write-Host "Copied managed plugin: $CoreMathDll -> $ManagedPluginsDir"
