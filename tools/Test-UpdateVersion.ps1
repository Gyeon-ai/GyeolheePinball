$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'UpdateVersion.ps1')

function Assert-Version([string]$Current, [string]$Expected) {
    $actual = Get-NextUpdateVersion ([Version]$Current)
    if ($actual.ToString() -ne $Expected) {
        throw "Version increment failed: $Current -> $actual (expected $Expected)"
    }
}

Assert-Version '1.4.4.2' '1.4.4.3'
Assert-Version '1.4.4.8' '1.4.4.9'
Assert-Version '1.4.4.9' '1.4.5.0'

try {
    Get-NextUpdateVersion ([Version]'1.4.4.10') | Out-Null
    throw 'An out-of-range revision was accepted.'
}
catch {
    if ($_.Exception.Message -eq 'An out-of-range revision was accepted.') {
        throw
    }
}

Write-Host 'Version increment and 9-to-next-build rollover passed.'
