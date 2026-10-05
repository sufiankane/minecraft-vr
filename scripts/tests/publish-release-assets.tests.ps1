#requires -Version 5.1
<#
.SYNOPSIS
    Unit-ish checks for the release-asset REST helpers (TD-036).

.DESCRIPTION
    Dot-sources scripts/lib/github-release-assets.ps1 and asserts the exact API
    and upload URIs and headers. No network access and no write access: this is
    the part of the attach fallback that can be verified without a release.
    Run: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/tests/publish-release-assets.tests.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\github-release-assets.ps1')

$script:Failures = 0
$script:Checks = 0

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)]$Actual
    )

    $script:Checks++
    if ($Expected -ne $Actual) {
        $script:Failures++
        Write-Host "FAIL: $Name`n  expected: $Expected`n  actual:   $Actual" -ForegroundColor Red
    }
    else {
        Write-Host "ok: $Name"
    }
}

Assert-Equal 'default api base is api.github.com' `
    'https://api.github.com' `
    (Get-GitHubApiBaseUri -ServerUrl '' -ApiUrl '')

Assert-Equal 'api url wins when provided' `
    'https://api.github.com' `
    (Get-GitHubApiBaseUri -ApiUrl 'https://api.github.com/')

Assert-Equal 'GitHub Enterprise server derives /api/v3' `
    'https://ghe.example.test/api/v3' `
    (Get-GitHubApiBaseUri -ServerUrl 'https://ghe.example.test' -ApiUrl '')

Assert-Equal 'uploads base for github.com' `
    'https://uploads.github.com' `
    (Get-GitHubUploadsBaseUri -ApiBase 'https://api.github.com')

Assert-Equal 'uploads base for GitHub Enterprise' `
    'https://ghe.example.test/api/v3/uploads' `
    (Get-GitHubUploadsBaseUri -ApiBase 'https://ghe.example.test/api/v3')

Assert-Equal 'release by tag uri' `
    'https://api.github.com/repos/acme/cubeglass/releases/tags/v0.1.0' `
    (Get-ReleaseByTagUri -ApiBase 'https://api.github.com' -Repository 'acme/cubeglass' -Tag 'v0.1.0')

Assert-Equal 'release asset delete uri' `
    'https://api.github.com/repos/acme/cubeglass/releases/assets/42' `
    (Get-ReleaseAssetUri -ApiBase 'https://api.github.com' -Repository 'acme/cubeglass' -AssetId 42)

Assert-Equal 'release asset upload uri' `
    'https://uploads.github.com/repos/acme/cubeglass/releases/7/assets?name=Cubeglass-windows-x64.zip' `
    (Get-ReleaseAssetUploadUri -UploadsBase 'https://uploads.github.com' -Repository 'acme/cubeglass' -ReleaseId 7 -AssetName 'Cubeglass-windows-x64.zip')

Assert-Equal 'asset names are url-encoded' `
    'https://uploads.github.com/repos/acme/cubeglass/releases/7/assets?name=a%20b%2Bc.zip' `
    (Get-ReleaseAssetUploadUri -UploadsBase 'https://uploads.github.com' -Repository 'acme/cubeglass' -ReleaseId 7 -AssetName 'a b+c.zip')

$headers = Get-GitHubAuthHeaders -Token 'token-abc'
Assert-Equal 'authorization header is a bearer token' 'Bearer token-abc' $headers.Authorization
Assert-Equal 'accept header' 'application/vnd.github+json' $headers.Accept
Assert-Equal 'api version header' '2022-11-28' $headers['X-GitHub-Api-Version']

# An HTTP error record without a response must not throw while probing status.
$fake = New-Object System.Management.Automation.ErrorRecord(
    (New-Object System.InvalidOperationException('no response')),
    'fake',
    [System.Management.Automation.ErrorCategory]::NotSpecified,
    $null)
Assert-Equal 'status probe tolerates a missing response' 0 (Get-HttpStatusCode -ErrorRecord $fake)

Write-Host ''
if ($script:Failures -gt 0) {
    Write-Host "$($script:Failures) of $($script:Checks) checks FAILED." -ForegroundColor Red
    exit 1
}
Write-Host "All $($script:Checks) release-asset helper checks passed." -ForegroundColor Green
exit 0
