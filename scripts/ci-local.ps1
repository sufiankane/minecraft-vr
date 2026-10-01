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
      6. unity        - Unity EditMode test through the Unity CLI (unless
                        -SkipUnity is passed).

    Every command is echoed before it runs. The script exits non-zero on the
    first failure and prints a final PASS/FAIL summary per lane. It works from a
    path containing spaces (every path is passed as a quoted argument, never
    interpolated into a shell string).

.PARAMETER SkipUnity
    Skip the Unity EditMode lane (for machines without the Unity editor).

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
$UnityResults = Join-Path $env:TEMP 'cg-unity-results.xml'

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
            # The Unity CLI resolves the project argument against the current
            # directory and mangles Windows absolute paths, so run from the repo
            # root with the canonical relative path (see docs/toolchains.md).
            Push-Location $RepoRoot
            try {
                Invoke-Checked 'unity test unity/Cubeglass --mode EditMode --non-interactive' {
                    & $UnityExe test 'unity\Cubeglass' --mode EditMode --non-interactive --output $UnityResults
                }
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
