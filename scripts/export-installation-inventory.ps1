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
    $token = $null
    $headers = $null
    try {
        $token = [System.Net.NetworkCredential]::new('', $AccessToken).Password
        $headers = @{ Authorization = "Bearer $token" }
        $response = Invoke-RestMethod -Uri "$($ApiBaseUri.TrimEnd('/'))/api/system/info" -Headers $headers -Method Get
    }
    finally {
        $headers = $null
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
$inventoryProperty = $snapshot.PSObject.Properties['inventory']
$snapshotSourceProperty = $snapshot.PSObject.Properties['snapshotSource']
$snapshotExportedAtProperty = $snapshot.PSObject.Properties['snapshotExportedAt']
if (
    $snapshot.formatVersion -ne 1 -or
    $snapshot.snapshotOrigin -ne 'Imported' -or
    $null -eq $inventoryProperty -or
    $null -eq $snapshotSourceProperty -or
    $null -eq $snapshotExportedAtProperty -or
    $null -eq $inventoryProperty.Value
) {
    throw 'The file is not a supported imported installation inventory snapshot.'
}

$inventory = $inventoryProperty.Value
$inventory | Add-Member -NotePropertyName snapshotOrigin -NotePropertyValue 'Imported' -Force
$inventory | Add-Member -NotePropertyName snapshotSource -NotePropertyValue $snapshotSourceProperty.Value -Force
$inventory | Add-Member -NotePropertyName snapshotExportedAt -NotePropertyValue $snapshotExportedAtProperty.Value -Force
$snapshot
