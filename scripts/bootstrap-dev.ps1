#requires -Version 5.1
<#
.SYNOPSIS
    Idempotent developer bootstrap and toolchain version gate for Cubeglass.

.DESCRIPTION
    Without -Check, installs the pinned developer toolchain with winget, clones
    and bootstraps vcpkg, and prints the cloned commit.

    With -Check, parses docs/toolchains.md, verifies every pin, prints a
    PASS/FAIL table, and exits 1 if any pin is not satisfied.

.PARAMETER Check
    Verify pins instead of installing.

.PARAMETER ExpectedOverride
    Forces the expected version for one tool for the duration of a -Check run,
    e.g. -ExpectedOverride "cmake=99.0.0". Installs are never affected.
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [string]$ExpectedOverride = ''
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ToolchainsPath = Join-Path $RepoRoot 'docs\toolchains.md'
$VcpkgDir = Join-Path $env:USERPROFILE 'vcpkg'
$VcpkgUrl = 'https://github.com/microsoft/vcpkg'
$WingetPackages = @(
    'Kitware.CMake',
    'Ninja-build.Ninja',
    'LLVM.LLVM',
    'Microsoft.DotNet.SDK.10',
    'GitHub.cli'
)

function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
    $extra = @(
        'C:\Program Files\LLVM\bin',
        'C:\Program Files\CMake\bin',
        'C:\Program Files\GitHub CLI',
        'C:\Program Files\nodejs',
        'C:\Python314',
        'C:\Program Files\dotnet'
    )
    foreach ($dir in $extra) {
        if ((Test-Path $dir) -and ($env:Path -notlike "*$dir*")) {
            $env:Path = "$env:Path;$dir"
        }
    }
}

function Get-ToolKey {
    param([string]$Tool)
    $k = ($Tool.ToLower() -replace '[^a-z0-9]', '')
    switch ($k) {
        'cmake' { return 'cmake' }
        'ninja' { return 'ninja' }
        'clangformat' { return 'clang-format' }
        'clangtidy' { return 'clang-tidy' }
        'vsbuildtools' { return 'vs-buildtools' }
        'vs' { return 'vs-buildtools' }
        { $_ -in 'netsdk', 'dotnetsdk', 'dotnet' } { return 'dotnet' }
        'vcpkg' { return 'vcpkg' }
        'python' { return 'python' }
        { $_ -in 'unityeditor', 'unity' } { return 'unity' }
        { $_ -in 'githubcli', 'gh' } { return 'gh' }
        { $_ -in 'node', 'nodejs' } { return 'node' }
        default { return $k }
    }
}

function Get-VswherePath {
    $candidate = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $candidate) { return $candidate }
    return $null
}

function Get-InstalledVersion {
    param([string]$Key)

    switch ($Key) {
        'cmake' {
            $cmd = Get-Command cmake -ErrorAction SilentlyContinue
            if (-not $cmd) { return $null }
            $out = (& $cmd.Source --version 2>&1 | Select-Object -First 1)
            if ("$out" -match 'cmake version\s+(\S+)') { return $Matches[1] }
            return $null
        }
        'ninja' {
            $cmd = Get-Command ninja -ErrorAction SilentlyContinue
            if (-not $cmd) { return $null }
            $out = (& $cmd.Source --version 2>&1 | Select-Object -First 1)
            return "$out".Trim()
        }
        'clang-format' {
            $cmd = Get-Command clang-format -ErrorAction SilentlyContinue
            if (-not $cmd) { return $null }
            $out = (& $cmd.Source --version 2>&1 | Out-String)
            if ($out -match 'clang-format version\s+(\S+)') { return $Matches[1] }
            return $null
        }
        'clang-tidy' {
            $cmd = Get-Command clang-tidy -ErrorAction SilentlyContinue
            if (-not $cmd) { return $null }
            $out = (& $cmd.Source --version 2>&1 | Out-String)
            if ($out -match 'LLVM version\s+(\S+)') { return $Matches[1] }
            return $null
        }
        'vs-buildtools' {
            $vswhere = Get-VswherePath
            if (-not $vswhere) { return $null }
            $out = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion 2>&1
            if ($LASTEXITCODE -ne 0) { return $null }
            $out = "$out".Trim()
            if ([string]::IsNullOrWhiteSpace($out)) { return $null }
            return $out
        }
        'dotnet' {
            $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
            if (-not $cmd) { return $null }
            $out = & $cmd.Source --version 2>&1
            if ($LASTEXITCODE -ne 0) { return $null }
            return "$out".Trim()
        }
        'vcpkg' {
            if (-not (Test-Path (Join-Path $VcpkgDir '.git'))) { return $null }
            $out = & git -C $VcpkgDir rev-parse HEAD 2>&1
            if ($LASTEXITCODE -ne 0) { return $null }
            return "$out".Trim()
        }
        'python' {
            $cmd = Get-Command python -ErrorAction SilentlyContinue
            if (-not $cmd) { return $null }
            $out = & $cmd.Source --version 2>&1
            if ("$out" -match 'Python\s+(\S+)') { return $Matches[1] }
            return $null
        }
        'gh' {
            $cmd = Get-Command gh -ErrorAction SilentlyContinue
            if (-not $cmd) { return $null }
            $out = (& $cmd.Source --version 2>&1 | Select-Object -First 1)
            if ("$out" -match 'gh version\s+(\S+)') { return $Matches[1] }
            return $null
        }
        'node' {
            $cmd = Get-Command node -ErrorAction SilentlyContinue
            if (-not $cmd) { return $null }
            $out = (& $cmd.Source --version 2>&1 | Select-Object -First 1)
            $out = "$out".Trim()
            return ($out -replace '^v', '')
        }
        default { return $null }
    }
}

function Compare-VersionAtLeast {
    param([string]$Installed, [string]$Required)
    $i = @([regex]::Matches($Installed, '\d+') | ForEach-Object { [int]$_.Value })
    $r = @([regex]::Matches($Required, '\d+') | ForEach-Object { [int]$_.Value })
    if ($i.Count -eq 0 -or $r.Count -eq 0) { return ($Installed -eq $Required) }
    $n = [Math]::Max($i.Count, $r.Count)
    for ($x = 0; $x -lt $n; $x++) {
        $iv = 0
        if ($x -lt $i.Count) { $iv = $i[$x] }
        $rv = 0
        if ($x -lt $r.Count) { $rv = $r[$x] }
        if ($iv -gt $rv) { return $true }
        if ($iv -lt $rv) { return $false }
    }
    return $true
}

function Get-Pins {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        throw "Pin table not found: $Path"
    }
    $pins = @()
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith('|')) { continue }
        $cells = $trimmed.Trim('|') -split '\|'
        if ($cells.Count -lt 4) { continue }
        $tool = $cells[0].Trim()
        if ($tool -eq '' -or $tool -eq 'Tool') { continue }
        if ($tool -match '^-+$') { continue }
        $pins += [pscustomobject]@{
            Tool     = $tool
            Required = $cells[1].Trim()
            Check    = $cells[2].Trim()
            Install  = $cells[3].Trim()
        }
    }
    return $pins
}

function Test-Pin {
    param([pscustomobject]$Pin, [string]$Key, [string]$Required)
    if ($Key -eq 'unity') {
        $dir = Join-Path 'C:\Program Files\Unity\Hub\Editor' $Required
        if (Test-Path $dir) {
            return [pscustomobject]@{ Installed = $Required; Pass = $true }
        }
        return [pscustomobject]@{ Installed = '<missing>'; Pass = $false }
    }
    if ($Key -eq 'vcpkg') {
        $installed = Get-InstalledVersion -Key $Key
        if ($null -eq $installed) {
            return [pscustomobject]@{ Installed = '<missing>'; Pass = $false }
        }
        $pass = ($installed -eq $Required) -or $installed.StartsWith($Required)
        return [pscustomobject]@{ Installed = $installed; Pass = $pass }
    }
    $installed = Get-InstalledVersion -Key $Key
    if ($null -eq $installed) {
        return [pscustomobject]@{ Installed = '<missing>'; Pass = $false }
    }
    return [pscustomobject]@{ Installed = $installed; Pass = (Compare-VersionAtLeast -Installed $installed -Required $Required) }
}

function Install-WingetPackage {
    param([string]$Id)
    $listing = ((& winget list --id $Id --exact --accept-source-agreements 2>&1) -join "`n")
    if (($listing -match [regex]::Escape($Id)) -and ($listing -notmatch 'No installed package found')) {
        Write-Output "[skip] $Id already installed"
        return
    }
    Write-Output "[install] $Id"
    & winget install --id $Id --exact --silent --accept-source-agreements --accept-package-agreements 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "winget install failed for $Id (exit $LASTEXITCODE)"
    }
}

Update-SessionPath

$pins = Get-Pins -Path $ToolchainsPath

$requiredToolKeys = @(
    'cmake', 'ninja', 'clang-format', 'clang-tidy', 'vs-buildtools',
    'dotnet', 'vcpkg', 'python', 'unity', 'gh', 'node'
)
if ($pins.Count -eq 0) {
    [Console]::Error.WriteLine("Pin table '$ToolchainsPath' yielded no pins. Refusing to report success.")
    exit 1
}
$parsedKeys = @($pins | ForEach-Object { Get-ToolKey -Tool $_.Tool })
$missingKeys = @($requiredToolKeys | Where-Object { $_ -notin $parsedKeys })
if ($missingKeys.Count -gt 0) {
    [Console]::Error.WriteLine("Pin table '$ToolchainsPath' is missing required rows: " + ($missingKeys -join ', '))
    exit 1
}

$vcpkgPin = ($pins | Where-Object { (Get-ToolKey -Tool $_.Tool) -eq 'vcpkg' } | Select-Object -First 1).Required

if (-not $Check) {
    foreach ($id in $WingetPackages) {
        Install-WingetPackage -Id $id
    }

    $vcpkgNeedsBootstrap = $false
    if (-not (Test-Path (Join-Path $VcpkgDir '.git'))) {
        Write-Output "[install] cloning vcpkg to $VcpkgDir"
        & git clone $VcpkgUrl $VcpkgDir 2>&1
        if ($LASTEXITCODE -ne 0) { throw "vcpkg clone failed (exit $LASTEXITCODE)" }
        $vcpkgNeedsBootstrap = $true
    }
    else {
        Write-Output "[skip] vcpkg already cloned at $VcpkgDir"
    }

    $currentHead = "$( & git -C $VcpkgDir rev-parse HEAD 2>&1 )".Trim()
    if ($currentHead -ne $vcpkgPin) {
        Write-Output "[checkout] vcpkg $currentHead -> baseline $vcpkgPin"
        & git -C $VcpkgDir checkout --quiet $vcpkgPin 2>&1
        if ($LASTEXITCODE -ne 0) { throw "vcpkg checkout of baseline $vcpkgPin failed (exit $LASTEXITCODE)" }
        $currentHead = "$( & git -C $VcpkgDir rev-parse HEAD 2>&1 )".Trim()
        if ($currentHead -ne $vcpkgPin) { throw "vcpkg HEAD '$currentHead' does not match baseline '$vcpkgPin' after checkout" }
        $vcpkgNeedsBootstrap = $true
    }
    else {
        Write-Output "[skip] vcpkg already at baseline $vcpkgPin"
    }

    if ($vcpkgNeedsBootstrap -or -not (Test-Path (Join-Path $VcpkgDir 'vcpkg.exe'))) {
        Write-Output "[install] bootstrapping vcpkg"
        & (Join-Path $VcpkgDir 'bootstrap-vcpkg.bat') -disableMetrics 2>&1
        if ($LASTEXITCODE -ne 0) { throw "vcpkg bootstrap failed (exit $LASTEXITCODE)" }
    }
    else {
        Write-Output "[skip] vcpkg already bootstrapped"
    }

    $head = & git -C $VcpkgDir rev-parse HEAD 2>&1
    Write-Output "vcpkg HEAD: $head"
    Write-Output "Bootstrap complete. Run: powershell -File scripts/bootstrap-dev.ps1 -Check"
    exit 0
}

$overrideKey = $null
$overrideVersion = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedOverride)) {
    if ($ExpectedOverride -match '^\s*([^=]+?)\s*=\s*(.+?)\s*$') {
        $overrideKey = Get-ToolKey -Tool $Matches[1]
        $overrideVersion = $Matches[2]
    }
    else {
        [Console]::Error.WriteLine("Invalid -ExpectedOverride '$ExpectedOverride'. Expected format '<tool>=<version>'.")
        exit 1
    }
}

$results = @()
foreach ($pin in $pins) {
    $key = Get-ToolKey -Tool $pin.Tool
    $required = $pin.Required
    $overridden = $false
    if ($null -ne $overrideKey -and $key -eq $overrideKey) {
        $required = $overrideVersion
        $overridden = $true
    }
    $pinResult = Test-Pin -Pin $pin -Key $key -Required $required
    $status = 'FAIL'
    if ($pinResult.Pass) { $status = 'PASS' }
    $requiredLabel = $required
    if ($overridden) { $requiredLabel = "$required (override)" }
    $results += [pscustomobject]@{
        Tool      = $pin.Tool
        Required  = $requiredLabel
        Installed = $pinResult.Installed
        Pass      = $pinResult.Pass
        Status    = $status
    }
}

$wTool = 4
$wReq = 8
$wInst = 9
foreach ($r in $results) {
    if ($r.Tool.Length -gt $wTool) { $wTool = $r.Tool.Length }
    if ($r.Required.Length -gt $wReq) { $wReq = $r.Required.Length }
    if ($r.Installed.Length -gt $wInst) { $wInst = $r.Installed.Length }
}

Write-Output ''
Write-Output ("{0}  {1}  {2}  {3}" -f 'TOOL'.PadRight($wTool), 'REQUIRED'.PadRight($wReq), 'INSTALLED'.PadRight($wInst), 'STATUS')
Write-Output ("{0}  {1}  {2}  {3}" -f ('-' * $wTool), ('-' * $wReq), ('-' * $wInst), ('-' * 6))
foreach ($r in $results) {
    Write-Output ("{0}  {1}  {2}  {3}" -f $r.Tool.PadRight($wTool), $r.Required.PadRight($wReq), $r.Installed.PadRight($wInst), $r.Status)
}
Write-Output ''

$failures = @($results | Where-Object { -not $_.Pass })
if ($failures.Count -gt 0) {
    foreach ($f in $failures) {
        Write-Output ("FAIL {0}: required {1} but found {2}" -f $f.Tool, $f.Required, $f.Installed)
    }
    exit 1
}

Write-Output 'All toolchain pins satisfied.'
exit 0
