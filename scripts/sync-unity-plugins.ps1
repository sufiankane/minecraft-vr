#requires -Version 5.1
<#
.SYNOPSIS
    Build and copy the managed Unity plugins into the Cubeglass project.

.DESCRIPTION
    Unity cannot resolve an assembly-definition reference to the Cubeglass
    .NET libraries because they live outside the Unity project. Instead, the
    packages load them as managed plugins from
    `unity/Cubeglass/Assets/Plugins/managed`, which Unity references
    automatically (the runtime asmdefs keep `overrideReferences = false`, so
    they need no explicit DLL references; test asmdefs set it to true and list
    the DLLs in `precompiledReferences`). This script:

      1. builds `dotnet/src/Streaming` in Release (netstandard2.1), which also
         builds its CoreMath, Voxel and Mesh project references, and
         `dotnet/src/Gameplay` (netstandard2.1), which builds CoreMath and
         Voxel again;
      2. copies `Cubeglass.CoreMath.dll`, `Cubeglass.Voxel.dll`,
         `Cubeglass.Mesh.dll` and `Cubeglass.Streaming.dll` to
         `unity/Cubeglass/Assets/Plugins/managed/` and `Cubeglass.Gameplay.dll`
         from its own output (it is not a Streaming reference).

    It fails loudly when the build fails or any expected DLL is missing, the
    same way `scripts/ci-local.ps1` fails loudly for the native bridge DLL.
    The copied DLLs (and their generated .meta files) are git-ignored.

    With `-IncludeTestSupport`, the native build outputs are staged as well:
    `cg_unity_bridge.dll` and the test-only `cg_bridge_test_support.dll`
    (TD-004/TD-067) from `cpp/build/windows-msvc/bridge`. The switch is for
    Unity test runs (`scripts/ci-local.ps1`); the release workflow calls this
    script without it, so the test-only library can never reach a release
    artefact.

    `System.Text.Json.dll` is deliberately NOT copied: Cubeglass.Voxel only
    needs it for the optional `BlockRegistry` JSON path, which the Unity
    runtime never touches (Unity's runtime does not ship that assembly). The
    rendering package meshes through `SliceBlockRegistry`, a code-built copy
    of the S7 terrain block definitions; the JSON remains the source of truth
    for the pure .NET modules.

.EXAMPLE
    powershell -File scripts/sync-unity-plugins.ps1

.EXAMPLE
    powershell -File scripts/sync-unity-plugins.ps1 -IncludeTestSupport
#>
[CmdletBinding()]
param(
    [switch]$IncludeTestSupport
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$StreamingProject = Join-Path $RepoRoot 'dotnet\src\Streaming\Cubeglass.Streaming.csproj'
$StreamingOutput = Join-Path $RepoRoot 'dotnet\src\Streaming\bin\Release\netstandard2.1'
$GameplayProject = Join-Path $RepoRoot 'dotnet\src\Gameplay\Cubeglass.Gameplay.csproj'
$GameplayOutput = Join-Path $RepoRoot 'dotnet\src\Gameplay\bin\Release\netstandard2.1'
$ManagedPluginsDir = Join-Path $RepoRoot 'unity\Cubeglass\Assets\Plugins\managed'

$PluginNames = @(
    'Cubeglass.CoreMath.dll',
    'Cubeglass.Voxel.dll',
    'Cubeglass.Mesh.dll',
    'Cubeglass.Streaming.dll'
)

Write-Host ">>> dotnet build '$StreamingProject' --configuration Release" -ForegroundColor Cyan
& dotnet build $StreamingProject --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed (exit $LASTEXITCODE): $StreamingProject"
}

Write-Host ">>> dotnet build '$GameplayProject' --configuration Release" -ForegroundColor Cyan
& dotnet build $GameplayProject --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed (exit $LASTEXITCODE): $GameplayProject"
}

New-Item -ItemType Directory -Path $ManagedPluginsDir -Force | Out-Null
foreach ($pluginName in $PluginNames) {
    $source = Join-Path $StreamingOutput $pluginName
    if (-not (Test-Path -LiteralPath $source)) {
        throw "managed plugin not found at '$source' after the Release build; check the Streaming target framework and output path"
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $ManagedPluginsDir $pluginName) -Force
    Write-Host "Copied managed plugin: $source -> $ManagedPluginsDir"
}

$gameplaySource = Join-Path $GameplayOutput 'Cubeglass.Gameplay.dll'
if (-not (Test-Path -LiteralPath $gameplaySource)) {
    throw "managed plugin not found at '$gameplaySource' after the Release build; check the Gameplay target framework and output path"
}

Copy-Item -LiteralPath $gameplaySource -Destination (Join-Path $ManagedPluginsDir 'Cubeglass.Gameplay.dll') -Force
Write-Host "Copied managed plugin: $gameplaySource -> $ManagedPluginsDir"

if ($IncludeTestSupport) {
    # TD-004/TD-067: stage the native build outputs for Unity test runs. The
    # production reader lives in cg_unity_bridge.dll; the test-only writer in
    # cg_bridge_test_support.dll. Both are git-ignored build outputs. Callers
    # that ship a player (the release workflow) never pass this switch, so the
    # test-only library cannot reach a release artefact.
    $nativeBuildDir = Join-Path $RepoRoot 'cpp\build\windows-msvc\bridge'
    $unityNativeDir = Join-Path $RepoRoot 'unity\Cubeglass\Assets\Plugins\win-x64'
    New-Item -ItemType Directory -Path $unityNativeDir -Force | Out-Null
    foreach ($nativeName in @('cg_unity_bridge.dll', 'cg_bridge_test_support.dll')) {
        $nativeSource = Join-Path $nativeBuildDir $nativeName
        if (-not (Test-Path -LiteralPath $nativeSource)) {
            throw "native plugin not found at '$nativeSource'; build it first (cmake --build --preset windows-msvc --target cg_bridge cg_bridge_test_support)"
        }

        Copy-Item -LiteralPath $nativeSource -Destination (Join-Path $unityNativeDir $nativeName) -Force
        Write-Host "Copied native plugin: $nativeSource -> $unityNativeDir"
    }
}
