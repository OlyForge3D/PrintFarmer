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

function Test-InventoryObject {
    param([object]$Value)

    return $Value -is [System.Management.Automation.PSCustomObject]
}

if ($PSCmdlet.ParameterSetName -eq 'Export') {
    try {
        $token = [System.Net.NetworkCredential]::new('', $AccessToken).Password
        $headers = @{ Authorization = "Bearer $token" }
        $response = Invoke-RestMethod -Uri "$($ApiBaseUri.TrimEnd('/'))/api/system/info" -Headers $headers -Method Get

        if (-not (Test-InventoryObject $response)) {
            throw 'The server did not return an admin inventory.'
        }

        $inventoryProperty = $response.PSObject.Properties['inventory']
        if ($null -eq $inventoryProperty -or -not (Test-InventoryObject $inventoryProperty.Value)) {
            throw 'The server did not return an admin inventory.'
        }

        $snapshot = [ordered]@{
            formatVersion = 1
            snapshotOrigin = 'Imported'
            snapshotSource = 'operator-export'
            snapshotExportedAt = [DateTimeOffset]::UtcNow.ToString('O')
            inventory = $inventoryProperty.Value
        }
        [System.IO.File]::WriteAllText(
            $OutputPath,
            ($snapshot | ConvertTo-Json -Depth 20),
            [System.Text.UTF8Encoding]::new($false))
    }
    finally {
        $headers = $null
        $token = $null
    }

    Write-Output "Exported redacted inventory to $OutputPath"
    exit 0
}

$snapshot = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
if (-not (Test-InventoryObject $snapshot)) {
    throw 'The file is not a supported imported installation inventory snapshot.'
}

$formatVersionProperty = $snapshot.PSObject.Properties['formatVersion']
$snapshotOriginProperty = $snapshot.PSObject.Properties['snapshotOrigin']
$inventoryProperty = $snapshot.PSObject.Properties['inventory']
$snapshotSourceProperty = $snapshot.PSObject.Properties['snapshotSource']
$snapshotExportedAtProperty = $snapshot.PSObject.Properties['snapshotExportedAt']
if (
    $null -eq $formatVersionProperty -or
    $null -eq $snapshotOriginProperty -or
    $null -eq $inventoryProperty -or
    $null -eq $snapshotSourceProperty -or
    $null -eq $snapshotExportedAtProperty -or
    $formatVersionProperty.Value -ne 1 -or
    $snapshotOriginProperty.Value -ne 'Imported' -or
    -not (Test-InventoryObject $inventoryProperty.Value)
) {
    throw 'The file is not a supported imported installation inventory snapshot.'
}

$inventory = $inventoryProperty.Value
$inventory | Add-Member -NotePropertyName snapshotOrigin -NotePropertyValue 'Imported' -Force
$inventory | Add-Member -NotePropertyName snapshotSource -NotePropertyValue $snapshotSourceProperty.Value -Force
$inventory | Add-Member -NotePropertyName snapshotExportedAt -NotePropertyValue $snapshotExportedAtProperty.Value -Force
$inventory | Add-Member -NotePropertyName eligibility -NotePropertyValue 'Unknown' -Force
$inventory | Add-Member -NotePropertyName eligibilityReasons -NotePropertyValue @('ImportedSnapshotIsNotLiveObservation') -Force
$inventory | Add-Member -NotePropertyName readiness -NotePropertyValue $null -Force
$snapshot | Add-Member -NotePropertyName inventory -NotePropertyValue $inventory -Force
$snapshot
