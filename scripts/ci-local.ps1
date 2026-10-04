#requires -Version 5.1
<#
.SYNOPSIS
    Run every Cubeglass CI lane locally, in order, fail-fast.

.DESCRIPTION
    Reproduces the required CI gates from a repository checkout with a single
    command:

      1. python-env   - create python/.venv if missing, install development
                        requirements and the editable package;
      2. cpp-windows  - MSVC dev shell, cmake configure/build (windows-msvc) and
                        ctest (ci preset);
      3. dotnet       - dotnet test Cubeglass.sln --configuration Release;
      4. python       - ruff, strict mypy and pytest from python/;
      5. depcheck     - dependency-rule and licence gates from the repo root;
      6. unity        - build and copy the managed CoreMath plugin, copy
                        cg_unity_bridge.dll from the cpp-windows build into the
                        Unity project and run the Unity EditMode and PlayMode
                        tests through the Unity CLI, failing loudly on missing
                        results or assemblies, skipped tests or failed tests
                        (unless -SkipUnity is passed).

    Every command is echoed before it runs. The script exits non-zero on the
    first failure and prints a final PASS/FAIL summary per lane. It works from a
    path containing spaces (every path is passed as a quoted argument, never
    interpolated into a shell string).

.PARAMETER SkipUnity
    Skip the Unity EditMode and PlayMode lanes (for machines without the Unity
    editor).

.EXAMPLE
    powershell -File scripts/ci-local.ps1

.EXAMPLE
    powershell -File scripts/ci-local.ps1 -SkipUnity
#>
[CmdletBinding()]
param(
    [switch]$SkipUnity
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CppDir = Join-Path $RepoRoot 'cpp'
$DotnetDir = Join-Path $RepoRoot 'dotnet'
$PythonDir = Join-Path $RepoRoot 'python'
$VenvDir = Join-Path $PythonDir '.venv'
$VenvPython = Join-Path $VenvDir 'Scripts\python.exe'
$RequirementsDev = Join-Path $PythonDir 'requirements-dev.txt'
$UnityExe = Join-Path $env:LOCALAPPDATA 'Unity\bin\unity.exe'
$UnityResultsEditMode = Join-Path $env:TEMP 'cg-unity-editmode-results.xml'
$UnityResultsPlayMode = Join-Path $env:TEMP 'cg-unity-playmode-results.xml'
$BridgeDll = Join-Path $CppDir 'build\windows-msvc\bridge\cg_unity_bridge.dll'
$UnityPluginsDir = Join-Path $RepoRoot 'unity\Cubeglass\Assets\Plugins\win-x64'

$script:LaneResults = New-Object System.Collections.Generic.List[object]

function Write-Command {
    param([string]$Text)
    Write-Host ">>> $Text" -ForegroundColor Cyan
}

function Invoke-Checked {
    param([string]$Display, [scriptblock]$Action)
    Write-Command $Display
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "command failed (exit $LASTEXITCODE): $Display"
    }
}

function Invoke-Lane {
    param([string]$Name, [scriptblock]$Action)
    Write-Host ''
    Write-Host "===== LANE: $Name =====" -ForegroundColor Yellow
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $stopwatch.Stop()
        $seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        $script:LaneResults.Add([pscustomobject]@{ Lane = $Name; Status = 'PASS'; Seconds = $seconds })
        Write-Host "----- PASS: $Name ($seconds s)" -ForegroundColor Green
    }
    catch {
        $stopwatch.Stop()
        $seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        $script:LaneResults.Add([pscustomobject]@{ Lane = $Name; Status = 'FAIL'; Seconds = $seconds })
        Write-Host "----- FAIL: $Name ($seconds s)" -ForegroundColor Red
        throw
    }
}

function Assert-UnityResults {
    param([string]$Path, [string]$Mode, [string[]]$RequiredAssemblies)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Unity $Mode results not found at '$Path'"
    }
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $testRun = $document.SelectSingleNode('/test-run')
    if ($null -eq $testRun) {
        throw "Unity $Mode results at '$Path' contain no /test-run element"
    }
    $total = [int]$testRun.total
    $passed = [int]$testRun.passed
    $failed = [int]$testRun.failed
    $skipped = [int]$testRun.skipped
    Write-Host "Unity $Mode results: total=$total passed=$passed failed=$failed skipped=$skipped"
    if ($total -le 0) {
        throw "Unity $Mode results at '$Path' report no tests (total=$total); the suite did not run"
    }
    if ($skipped -gt 0) {
        throw "Unity $Mode results at '$Path' report $skipped skipped test(s); every test must run"
    }
    # Membership, not equality: extra suites (placeholder, CoreMath, future
    # packages) are allowed, but every required lane must be present.
    $assemblies = @($testRun.SelectNodes('.//test-suite[@type="Assembly"]') | ForEach-Object { $_.name })
    $missing = @($RequiredAssemblies | Where-Object { $assemblies -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "Unity $Mode results at '$Path' are missing the required assembly/assemblies '$($missing -join ', ')' (found: $($assemblies -join ', ')); wrong test mode or stale results"
    }
    if ($failed -gt 0) {
        throw "Unity $Mode reported $failed failed test(s); see '$Path'"
    }
}

function Write-Summary {
    Write-Host ''
    Write-Host '===== ci-local summary =====' -ForegroundColor Yellow
    foreach ($r in $script:LaneResults) {
        $color = if ($r.Status -eq 'PASS') { 'Green' } else { 'Red' }
        Write-Host ('{0,-13} {1,-4} {2,8} s' -f $r.Lane, $r.Status, $r.Seconds) -ForegroundColor $color
    }
}

Write-Host "ci-local: repo root $RepoRoot"

try {
    Invoke-Lane 'python-env' {
        if (-not (Test-Path $VenvDir)) {
            Invoke-Checked 'python -m venv python/.venv' { & python -m venv $VenvDir }
        }
        else {
            Write-Host 'python/.venv already exists; reusing it.'
        }
        if (-not (Test-Path $VenvPython)) {
            throw "virtual-environment interpreter not found at '$VenvPython'"
        }
        Invoke-Checked 'python -m pip install -r requirements-dev.txt' {
            & $VenvPython -m pip install -r $RequirementsDev
        }
        Invoke-Checked 'python -m pip install -e .' {
            & $VenvPython -m pip install -e $PythonDir
        }
    }

    Write-Command '. scripts/dev-shell.ps1'
    . (Join-Path $RepoRoot 'scripts\dev-shell.ps1')

    Invoke-Lane 'cpp-windows' {
        # Builds every target, including the cg_bridge shared library
        # (cg_unity_bridge.dll). Keep this lane before the unity lane, which
        # copies that DLL into the Unity project.
        Push-Location $CppDir
        try {
            Invoke-Checked 'cmake --preset windows-msvc' { & cmake --preset windows-msvc }
            Invoke-Checked 'cmake --build --preset windows-msvc' { & cmake --build --preset windows-msvc }
            Invoke-Checked 'ctest --preset ci' { & ctest --preset ci }
        }
        finally {
            Pop-Location
        }
    }

    Invoke-Lane 'dotnet' {
        Push-Location $DotnetDir
        try {
            Invoke-Checked 'dotnet test Cubeglass.sln --configuration Release' {
                & dotnet test Cubeglass.sln --configuration Release
            }
        }
        finally {
            Pop-Location
        }
    }

    Invoke-Lane 'python' {
        Push-Location $PythonDir
        try {
            Invoke-Checked 'python -m ruff check .' { & $VenvPython -m ruff check . }
            Invoke-Checked 'python -m mypy calib depcheck' { & $VenvPython -m mypy calib depcheck }
            Invoke-Checked 'python -m pytest' { & $VenvPython -m pytest }
        }
        finally {
            Pop-Location
        }
    }

    Invoke-Lane 'depcheck' {
        Push-Location $RepoRoot
        try {
            Invoke-Checked 'python -m depcheck --root .' { & $VenvPython -m depcheck --root . }
            Invoke-Checked 'python -m depcheck contracts --root .' { & $VenvPython -m depcheck contracts --root . }
            Invoke-Checked 'python -m depcheck licences --root .' { & $VenvPython -m depcheck licences --root . }
        }
        finally {
            Pop-Location
        }
    }

    if ($SkipUnity) {
        Write-Host ''
        Write-Host '===== LANE: unity (skipped: -SkipUnity) =====' -ForegroundColor Yellow
        $script:LaneResults.Add([pscustomobject]@{ Lane = 'unity'; Status = 'SKIP'; Seconds = 0 })
    }
    else {
        Invoke-Lane 'unity' {
            if (-not (Test-Path $UnityExe)) {
                throw "Unity CLI not found at '$UnityExe'"
            }
            # The native bridge tests P/Invoke cg_unity_bridge.dll from
            # Assets/Plugins/win-x64. Fail loudly when it is missing instead of
            # skipping: the cpp-windows lane above must have built it.
            if (-not (Test-Path -LiteralPath $BridgeDll)) {
                throw "native bridge DLL not found at '$BridgeDll'; run the C++ build first (cmake --build --preset windows-msvc), then re-run ci-local"
            }
            if (-not (Test-Path -LiteralPath $UnityPluginsDir)) {
                New-Item -ItemType Directory -Path $UnityPluginsDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $BridgeDll -Destination (Join-Path $UnityPluginsDir 'cg_unity_bridge.dll') -Force
            Write-Host "Copied native bridge DLL: $BridgeDll -> $UnityPluginsDir"
            # Cubeglass.CoreMath is a .NET library outside Unity, so the bridge
            # package loads it as a managed plugin instead of an asmdef
            # reference. The sync script builds it in Release and fails loudly
            # when the DLL is missing; Task 3 consumes UnityConvert from it.
            $SyncPlugins = Join-Path $PSScriptRoot 'sync-unity-plugins.ps1'
            Invoke-Checked 'scripts/sync-unity-plugins.ps1' { & $SyncPlugins }
            # The Unity CLI mishandles the project argument (it prepends the
            # current directory to an already-absolute path) when the project
            # has no Assets folder, so a pristine clone aborts before importing.
            # Unity always creates Assets on first import; create it up front so
            # the recorded relative invocation below resolves correctly.
            $unityAssets = Join-Path $RepoRoot 'unity\Cubeglass\Assets'
            if (-not (Test-Path $unityAssets)) {
                Write-Host "Creating missing Unity Assets folder: $unityAssets"
                New-Item -ItemType Directory -Path $unityAssets -Force | Out-Null
            }
            Push-Location $RepoRoot
            try {
                # The S6 calibration scene (Assets/Scenes/Calibration.unity) and
                # the S7 game scene (Assets/Scenes/Game.unity) are committed and
                # byte-stable; the PlayMode lane loads both directly. The game
                # scene smoke (GameScenePlayModeTests) lives in the same
                # Cubeglass.Unity.Rendering.PlayTests assembly, so this single
                # invocation covers it with no extra filter. Regenerating either
                # scene is a manual menu/batch action and is deliberately not
                # part of this per-commit lane.
                Invoke-Checked 'unity test unity/Cubeglass --mode EditMode --non-interactive' {
                    & $UnityExe test 'unity/Cubeglass' --mode EditMode --non-interactive --output $UnityResultsEditMode
                }
                Assert-UnityResults -Path $UnityResultsEditMode -Mode 'EditMode' -RequiredAssemblies @(
                    'Cubeglass.Unity.Bridge.Tests.dll',
                    'Cubeglass.Unity.Input.Tests.dll',
                    'Cubeglass.Unity.Rendering.Tests.dll')

                Invoke-Checked 'unity test unity/Cubeglass --mode PlayMode --non-interactive' {
                    & $UnityExe test 'unity/Cubeglass' --mode PlayMode --non-interactive --output $UnityResultsPlayMode
                }
                Assert-UnityResults -Path $UnityResultsPlayMode -Mode 'PlayMode' -RequiredAssemblies @(
                    'Cubeglass.Unity.Rendering.PlayTests.dll')
            }
            finally {
                Pop-Location
            }
        }
    }
}
catch {
    Write-Host ''
    Write-Host "ci-local FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Summary
    exit 1
}

Write-Summary
Write-Host ''
Write-Host 'ci-local: ALL LANES PASS' -ForegroundColor Green
exit 0
