function Get-ApprovedDotnetSdkVersion {
    $sdk = (Get-Content (Join-Path $PSScriptRoot '..\global.json') -Raw | ConvertFrom-Json).sdk
    if ($sdk.version -notmatch '^10\.0\.\d{3}$' -or [int]($sdk.version.Split('.')[2]) -lt 200 -or
        $sdk.rollForward -ne 'disable' -or $sdk.allowPrerelease -ne $false) {
        throw 'global.json must approve an exact .NET 10 SDK >=10.0.200 without prereleases'
    }
    return $sdk.version
}

function Get-ApprovedDotnetSdkTag {
    return "$(Get-ApprovedDotnetSdkVersion)-noble"
}

function Assert-ApprovedDotnetSdk {
    $approved = Get-ApprovedDotnetSdkVersion
    Push-Location (Join-Path $PSScriptRoot '..')
    try {
        $selected = & dotnet --version
        if ($LASTEXITCODE -ne 0 -or $selected -ne $approved) {
            throw "Selected SDK '$selected' differs from global.json ($approved)"
        }
        Write-Host ".NET SDK selected: $selected; approved: $approved"
    } finally {
        Pop-Location
    }
}
