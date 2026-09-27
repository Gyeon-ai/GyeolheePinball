function Get-NextUpdateVersion {
    param(
        [Parameter(Mandatory = $true)]
        [Version]$CurrentVersion
    )

    if ($CurrentVersion.Build -lt 0 -or $CurrentVersion.Revision -lt 0) {
        throw "A four-part version is required: $CurrentVersion"
    }
    if ($CurrentVersion.Revision -gt 9) {
        throw "The revision must be between 0 and 9: $CurrentVersion"
    }

    if ($CurrentVersion.Revision -lt 9) {
        return [Version]::new(
            $CurrentVersion.Major,
            $CurrentVersion.Minor,
            $CurrentVersion.Build,
            $CurrentVersion.Revision + 1)
    }

    if ($CurrentVersion.Build -ge 65534) {
        throw "The build component cannot be incremented safely: $CurrentVersion"
    }

    return [Version]::new(
        $CurrentVersion.Major,
        $CurrentVersion.Minor,
        $CurrentVersion.Build + 1,
        0)
}
