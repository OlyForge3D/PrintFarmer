[CmdletBinding(DefaultParameterSetName = 'Export')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Export')]
    [ValidatePattern('^https?://')]
    [string]$ApiBaseUri,

    [Parameter(Mandatory, ParameterSetName = 'Export')]
    [SecureString]$AccessToken,

    [Parameter(Mandatory, ParameterSetName = 'Export')]
    [string]$OutputPath,

    [Parameter(Mandatory, ParameterSetName = 'Import')]
    [string]$InputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ParameterSetName -eq 'Export') {
    $token = [System.Net.NetworkCredential]::new('', $AccessToken).Password
    try {
        $headers = @{ Authorization = "Bearer $token" }
        $response = Invoke-RestMethod -Uri "$($ApiBaseUri.TrimEnd('/'))/api/system/info" -Headers $headers -Method Get
    }
    finally {
        $token = $null
    }

    if ($null -eq $response.inventory) {
        throw 'The server did not return an admin inventory.'
    }

    $snapshot = [ordered]@{
        formatVersion = 1
        snapshotOrigin = 'Imported'
        snapshotSource = 'operator-export'
        snapshotExportedAt = [DateTimeOffset]::UtcNow.ToString('O')
        inventory = $response.inventory
    }
    $snapshot | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
    Write-Output "Exported redacted inventory to $OutputPath"
    exit 0
}

$snapshot = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
if ($snapshot.formatVersion -ne 1 -or $snapshot.snapshotOrigin -ne 'Imported' -or $null -eq $snapshot.inventory) {
    throw 'The file is not a supported imported installation inventory snapshot.'
}

$snapshot
