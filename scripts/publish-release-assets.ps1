#requires -Version 5.1
<#
.SYNOPSIS
    Attach release assets, preferring `gh` and falling back to the GitHub REST API.

.DESCRIPTION
    TD-036: `gh` is preferred when present, but it is not a hard dependency. When
    `gh` is missing (a fresh self-hosted runner), the script uses
    Invoke-RestMethod with GITHUB_TOKEN and the release-asset REST API:
    a missing release is a normal outcome (the tag is owner-gated), an existing
    asset is deleted before re-upload (the `gh --clobber` equivalent), and any
    other failure is fatal.

    The request-building helpers live in scripts/lib/github-release-assets.ps1
    and are unit-checked by scripts/tests/publish-release-assets.tests.ps1.

.PARAMETER Tag
    Release tag, e.g. v0.1.0.

.PARAMETER Assets
    One or more local file paths to attach.

.PARAMETER Repository
    owner/repo; defaults to GITHUB_REPOSITORY.

.PARAMETER Token
    Token with contents:write; defaults to GITHUB_TOKEN, then GH_TOKEN.

.EXAMPLE
    ./scripts/publish-release-assets.ps1 -Tag v0.1.0 -Assets dist/Cubeglass-windows-x64.zip
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string[]]$Assets,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Token = $(if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { $env:GITHUB_TOKEN } else { $env:GH_TOKEN }),
    [string]$ApiBase = '',
    [string]$UploadsBase = ''
)

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'lib\github-release-assets.ps1')

if ([string]::IsNullOrWhiteSpace($ApiBase)) {
    $ApiBase = Get-GitHubApiBaseUri
}
if ([string]::IsNullOrWhiteSpace($UploadsBase)) {
    $UploadsBase = Get-GitHubUploadsBaseUri -ApiBase $ApiBase
}

foreach ($asset in $Assets) {
    if (-not (Test-Path -LiteralPath $asset)) {
        Write-Host "::error::Release asset not found: $asset"
        exit 1
    }
}

if (Get-Command gh -ErrorAction SilentlyContinue) {
    Write-Host 'Using the GitHub CLI (gh) to attach release assets.'
    $ErrorActionPreference = 'Continue'
    $probe = gh release view $Tag 2>&1
    $releaseExists = $LASTEXITCODE -eq 0
    $global:LASTEXITCODE = 0
    if (-not $releaseExists) {
        Write-Host ("Release {0} does not exist yet (probe: {1}); the assets stay available as workflow artefacts." -f $Tag, (($probe | Out-String).Trim()))
        exit 0
    }
    $upload = gh release upload $Tag @Assets --clobber 2>&1
    $uploadStatus = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($uploadStatus -ne 0) {
        Write-Host ($upload | Out-String)
        Write-Host "::error::gh release upload failed with exit code $uploadStatus."
        exit 1
    }
    Write-Host ("Attached {0} to the {1} release with gh." -f (($Assets | ForEach-Object { Split-Path -Leaf $_ }) -join ', '), $Tag)
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    Write-Host '::error::gh is not available and neither GITHUB_TOKEN nor GH_TOKEN is set; cannot attach release assets. Set GITHUB_TOKEN (contents:write) on the attach job, or install gh.'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($Repository)) {
    Write-Host '::error::GITHUB_REPOSITORY (owner/repo) is not set and -Repository was not passed; cannot attach release assets.'
    exit 1
}

Write-Host 'gh is not available; using the GitHub REST API.'
$headers = Get-GitHubAuthHeaders -Token $Token
$releaseUri = Get-ReleaseByTagUri -ApiBase $ApiBase -Repository $Repository -Tag $Tag
try {
    $release = Invoke-RestMethod -Method Get -Uri $releaseUri -Headers $headers
}
catch {
    if ((Get-HttpStatusCode $_) -eq 404) {
        Write-Host ("Release {0} does not exist yet; the assets stay available as workflow artefacts." -f $Tag)
        exit 0
    }
    Write-Host "::error::GET $releaseUri failed: $($_.Exception.Message)"
    exit 1
}

$existingByName = @{}
foreach ($asset in @($release.assets)) {
    $existingByName[[string]$asset.name] = [long]$asset.id
}

foreach ($path in $Assets) {
    $name = Split-Path -Leaf $path
    if ($existingByName.ContainsKey($name)) {
        $deleteUri = Get-ReleaseAssetUri -ApiBase $ApiBase -Repository $Repository -AssetId $existingByName[$name]
        try {
            Invoke-RestMethod -Method Delete -Uri $deleteUri -Headers $headers | Out-Null
            Write-Host "Deleted the existing asset $name (--clobber equivalent)."
        }
        catch {
            Write-Host "::error::DELETE $deleteUri failed: $($_.Exception.Message)"
            exit 1
        }
    }
    $uploadUri = Get-ReleaseAssetUploadUri -UploadsBase $UploadsBase -Repository $Repository -ReleaseId ([long]$release.id) -AssetName $name
    try {
        Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $headers -ContentType 'application/octet-stream' -InFile $path | Out-Null
    }
    catch {
        Write-Host "::error::uploading $name to $uploadUri failed: $($_.Exception.Message)"
        exit 1
    }
    Write-Host "Uploaded $name to the $Tag release."
}
