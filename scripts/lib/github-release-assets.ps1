#requires -Version 5.1
<#
.SYNOPSIS
    Request-building helpers for the GitHub release-asset REST API (TD-036).

.DESCRIPTION
    Pure functions used by scripts/publish-release-assets.ps1 and asserted by
    scripts/tests/publish-release-assets.tests.ps1. They are dot-sourceable from
    both Windows PowerShell 5.1 and PowerShell 7 so the same code builds the
    API and uploads URIs on either release route.
#>

Set-StrictMode -Version Latest

function Get-GitHubApiBaseUri {
    param(
        [string]$ServerUrl = $env:GITHUB_SERVER_URL,
        [string]$ApiUrl = $env:GITHUB_API_URL
    )

    if (-not [string]::IsNullOrWhiteSpace($ApiUrl)) {
        return $ApiUrl.TrimEnd('/')
    }
    if (-not [string]::IsNullOrWhiteSpace($ServerUrl) -and $ServerUrl -ne 'https://github.com') {
        return ($ServerUrl.TrimEnd('/') + '/api/v3')
    }
    return 'https://api.github.com'
}

function Get-GitHubUploadsBaseUri {
    param([string]$ApiBase = (Get-GitHubApiBaseUri))

    if ($ApiBase -eq 'https://api.github.com') {
        return 'https://uploads.github.com'
    }
    return ($ApiBase.TrimEnd('/') + '/uploads')
}

function Get-ReleaseByTagUri {
    param(
        [Parameter(Mandatory = $true)][string]$ApiBase,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Tag
    )

    return ("{0}/repos/{1}/releases/tags/{2}" -f $ApiBase.TrimEnd('/'), $Repository, $Tag)
}

function Get-ReleaseAssetUri {
    param(
        [Parameter(Mandatory = $true)][string]$ApiBase,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][long]$AssetId
    )

    return ("{0}/repos/{1}/releases/assets/{2}" -f $ApiBase.TrimEnd('/'), $Repository, $AssetId)
}

function Get-ReleaseAssetUploadUri {
    param(
        [Parameter(Mandatory = $true)][string]$UploadsBase,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][long]$ReleaseId,
        [Parameter(Mandatory = $true)][string]$AssetName
    )

    $encoded = [uri]::EscapeDataString($AssetName)
    return ("{0}/repos/{1}/releases/{2}/assets?name={3}" -f $UploadsBase.TrimEnd('/'), $Repository, $ReleaseId, $encoded)
}

function Get-GitHubAuthHeaders {
    param([Parameter(Mandatory = $true)][string]$Token)

    return @{
        Authorization          = "Bearer $Token"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
}

function Get-HttpStatusCode {
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    $exception = $ErrorRecord.Exception
    if ($null -eq $exception) {
        return 0
    }
    # Strict mode makes a missing Response property an error, and a non-HTTP
    # exception (e.g. DNS failure) has none, so probe the property bag.
    $responseProperty = $exception.PSObject.Properties['Response']
    if ($null -eq $responseProperty -or $null -eq $responseProperty.Value) {
        return 0
    }
    return [int]$responseProperty.Value.StatusCode
}
