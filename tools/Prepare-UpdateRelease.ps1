param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReleaseNotes,

    [string]$MsBuildPath,

    [string]$SigningKeyPath = (Join-Path $env:LOCALAPPDATA 'Gyeona\TayoPinball\Signing\update-signing-key.dat'),

    [switch]$RebuildCurrentVersion
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$projects = @(
    [pscustomobject]@{
        Name = 'GyeolheePinball'
        AssemblyInfo = Join-Path $root 'GyeolheePinball\Properties\AssemblyInfo.cs'
        BuildExe = Join-Path $root 'build\Release\GyeolheePinball\GyeolheePinball.exe'
        ReleaseExe = Join-Path $root '결희 종겜핀볼.exe'
        ManifestKey = 'Standard'
        Url = 'https://github.com/Gyeon-ai/GyeolheePinball/raw/refs/heads/main/%EA%B2%B0%ED%9D%AC%20%EC%A2%85%EA%B2%9C%ED%95%80%EB%B3%BC.exe'
    },
    [pscustomobject]@{
        Name = 'GyeolheePinballAuto'
        AssemblyInfo = Join-Path $root 'GyeolheePinballAuto\Properties\AssemblyInfo.cs'
        BuildExe = Join-Path $root 'build\Release\GyeolheePinballAuto\GyeolheePinballAuto.exe'
        ReleaseExe = Join-Path $root '결희 종겜핀볼(자동).exe'
        ManifestKey = 'Auto'
        Url = 'https://github.com/Gyeon-ai/GyeolheePinball/raw/refs/heads/main/%EA%B2%B0%ED%9D%AC%20%EC%A2%85%EA%B2%9C%ED%95%80%EB%B3%BC%28%EC%9E%90%EB%8F%99%29.exe'
    }
)

$manifestPath = Join-Path $root 'update.json'
$manifestSignaturePath = Join-Path $root 'update.json.sig'
$publicKeyPath = Join-Path $root 'update-public-key.xml'
$buildScript = Join-Path $PSScriptRoot 'Build-Release.ps1'
$signatureTestScript = Join-Path $PSScriptRoot 'Test-UpdateSignature.ps1'
$versioningScript = Join-Path $PSScriptRoot 'UpdateVersion.ps1'
$numberedArtifactRoot = Join-Path $root 'dist\Release'
$backupDirectory = Join-Path ([IO.Path]::GetTempPath()) ('GyeolheePinballRelease-' + [Guid]::NewGuid().ToString('N'))
$utf8NoBom = New-Object Text.UTF8Encoding($false)
# The shared publisher key stays outside this repository; keep its original DPAPI entropy.
$entropy = [Text.Encoding]::UTF8.GetBytes('TayoPinball update signing key v1')
$manifestExisted = Test-Path -LiteralPath $manifestPath
$signatureExisted = Test-Path -LiteralPath $manifestSignaturePath
$existingNumberedArtifacts = @{}
$completed = $false

Add-Type -AssemblyName System.Security

if (!(Test-Path -LiteralPath $versioningScript)) {
    throw "Update version helper was not found: $versioningScript"
}
. $versioningScript

if (Test-Path -LiteralPath $numberedArtifactRoot) {
    Get-ChildItem -LiteralPath $numberedArtifactRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.exe' -or $_.Extension -eq '.pdb' } |
        ForEach-Object { $existingNumberedArtifacts[$_.FullName.ToLowerInvariant()] = $true }
}

function Resolve-MsBuild {
    param([string]$RequestedPath)

    if (![String]::IsNullOrWhiteSpace($RequestedPath)) {
        if (!(Test-Path -LiteralPath $RequestedPath)) {
            throw "MSBuild was not found: $RequestedPath"
        }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $knownPaths = @(
        'C:\Program1\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach ($knownPath in $knownPaths) {
        if (Test-Path -LiteralPath $knownPath) {
            return (Resolve-Path -LiteralPath $knownPath).Path
        }
    }

    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $resolved = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
        if (![String]::IsNullOrWhiteSpace($resolved) -and (Test-Path -LiteralPath $resolved)) {
            return (Resolve-Path -LiteralPath $resolved).Path
        }
    }

    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw 'MSBuild.exe was not found.'
}

function Get-AssemblyVersion {
    param([string]$Path)

    $content = [IO.File]::ReadAllText($Path)
    $match = [Regex]::Match($content, 'AssemblyVersion\("(?<version>\d+\.\d+\.\d+\.\d+)"\)')
    if (!$match.Success) {
        throw "AssemblyVersion was not found: $Path"
    }
    return [Version]$match.Groups['version'].Value
}

function Set-AssemblyVersion {
    param(
        [string]$Path,
        [Version]$Version
    )

    $content = [IO.File]::ReadAllText($Path)
    $versionText = $Version.ToString()
    $content = [Regex]::Replace(
        $content,
        'AssemblyVersion\("\d+\.\d+\.\d+\.\d+"\)',
        "AssemblyVersion(`"$versionText`")")
    $content = [Regex]::Replace(
        $content,
        'AssemblyFileVersion\("\d+\.\d+\.\d+\.\d+"\)',
        "AssemblyFileVersion(`"$versionText`")")
    [IO.File]::WriteAllText($Path, $content, $utf8NoBom)
}

function Backup-File {
    param([string]$Path)

    if (!(Test-Path -LiteralPath $Path)) {
        return
    }

    $relative = $Path.Substring($root.Length).TrimStart('\')
    $backupPath = Join-Path $backupDirectory $relative
    $backupParent = Split-Path -Parent $backupPath
    New-Item -ItemType Directory -Force -Path $backupParent | Out-Null
    Copy-Item -LiteralPath $Path -Destination $backupPath -Force
}

function Restore-File {
    param([string]$Path)

    $relative = $Path.Substring($root.Length).TrimStart('\')
    $backupPath = Join-Path $backupDirectory $relative
    if (Test-Path -LiteralPath $backupPath) {
        Copy-Item -LiteralPath $backupPath -Destination $Path -Force
    }
}

function New-RsaProvider {
    $parameters = New-Object Security.Cryptography.CspParameters
    $parameters.ProviderType = 24
    $provider = New-Object Security.Cryptography.RSACryptoServiceProvider($parameters)
    $provider.PersistKeyInCsp = $false
    return $provider
}

function Read-ProtectedPrivateKey {
    param([string]$Path)

    $protectedBytes = [IO.File]::ReadAllBytes($Path)
    $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
        $protectedBytes,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try {
        return [Text.Encoding]::UTF8.GetString($plainBytes)
    }
    finally {
        [Array]::Clear($plainBytes, 0, $plainBytes.Length)
    }
}

function Write-ManifestSignature {
    param(
        [string]$Manifest,
        [string]$Signature,
        [string]$PrivateKey,
        [string]$PublicKey
    )

    $manifestBytes = [IO.File]::ReadAllBytes($Manifest)
    $sha256Oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    $rsa = New-RsaProvider
    try {
        $rsa.FromXmlString((Read-ProtectedPrivateKey -Path $PrivateKey))
        $signatureBytes = $rsa.SignData($manifestBytes, $sha256Oid)
        [IO.File]::WriteAllText(
            $Signature,
            [Convert]::ToBase64String($signatureBytes) + [Environment]::NewLine,
            $utf8NoBom)
    }
    finally {
        $rsa.Dispose()
    }

    $publicRsa = New-RsaProvider
    try {
        $publicRsa.FromXmlString([IO.File]::ReadAllText($PublicKey))
        if (!$publicRsa.VerifyData($manifestBytes, $sha256Oid, $signatureBytes)) {
            throw 'The generated update signature does not match the embedded public key.'
        }
    }
    finally {
        $publicRsa.Dispose()
    }
}

function Remove-NewNumberedArtifacts {
    if (!(Test-Path -LiteralPath $numberedArtifactRoot)) {
        return
    }

    $safeRoot = [IO.Path]::GetFullPath($numberedArtifactRoot).TrimEnd('\') + '\'
    Get-ChildItem -LiteralPath $numberedArtifactRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.exe' -or $_.Extension -eq '.pdb' } |
        ForEach-Object {
            $fullPath = [IO.Path]::GetFullPath($_.FullName)
            if ($fullPath.StartsWith($safeRoot, [StringComparison]::OrdinalIgnoreCase) -and
                !$existingNumberedArtifacts.ContainsKey($fullPath.ToLowerInvariant())) {
                Remove-Item -LiteralPath $fullPath -Force
            }
        }
}

New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null

try {
    if (!(Test-Path -LiteralPath $SigningKeyPath)) {
        throw "Protected update signing key was not found: $SigningKeyPath"
    }
    if (!(Test-Path -LiteralPath $publicKeyPath)) {
        throw "Update public key was not found: $publicKeyPath"
    }
    if (!(Test-Path -LiteralPath $signatureTestScript)) {
        throw "Update signature test was not found: $signatureTestScript"
    }

    foreach ($project in $projects) {
        Backup-File $project.AssemblyInfo
        Backup-File $project.ReleaseExe
    }
    Backup-File $manifestPath
    Backup-File $manifestSignaturePath

    $versions = @($projects | ForEach-Object { Get-AssemblyVersion $_.AssemblyInfo })
    if ($versions[0] -ne $versions[1]) {
        throw "Project versions do not match: $($versions[0]) / $($versions[1])"
    }
    $nextVersion = if ($RebuildCurrentVersion) { $versions[0] } else { Get-NextUpdateVersion $versions[0] }

    if (Test-Path -LiteralPath $manifestPath) {
        $publishedManifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
        if ($nextVersion -le [Version]$publishedManifest.Version) {
            throw 'The release version must be newer than the existing signed manifest.'
        }
    }

    foreach ($project in $projects) {
        Set-AssemblyVersion -Path $project.AssemblyInfo -Version $nextVersion
    }

    $resolvedMsBuild = Resolve-MsBuild $MsBuildPath
    & $buildScript -Project All -Configuration Release -MsBuildPath $resolvedMsBuild
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }

    $releaseData = @{}
    foreach ($project in $projects) {
        if (!(Test-Path -LiteralPath $project.BuildExe)) {
            throw "Built executable was not found: $($project.BuildExe)"
        }

        $builtVersion = [Reflection.AssemblyName]::GetAssemblyName($project.BuildExe).Version
        if ($builtVersion -ne $nextVersion) {
            throw "Built version mismatch for $($project.Name): $builtVersion"
        }

        Copy-Item -LiteralPath $project.BuildExe -Destination $project.ReleaseExe -Force
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $project.ReleaseExe).Hash.ToUpperInvariant()
        $size = (Get-Item -LiteralPath $project.ReleaseExe).Length
        $releaseData[$project.ManifestKey] = [ordered]@{
            Url = $project.Url
            Sha256 = $hash
            Size = $size
        }
    }

    $manifest = [ordered]@{
        SchemaVersion = 2
        ProductId = 'Gyeon-ai/GyeolheePinball'
        Version = $nextVersion.ToString()
        ReleaseNotes = $ReleaseNotes.Trim()
        Standard = $releaseData['Standard']
        Auto = $releaseData['Auto']
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($manifestPath, $manifestJson + [Environment]::NewLine, $utf8NoBom)
    Write-ManifestSignature `
        -Manifest $manifestPath `
        -Signature $manifestSignaturePath `
        -PrivateKey $SigningKeyPath `
        -PublicKey $publicKeyPath
    & $signatureTestScript `
        -ManifestPath $manifestPath `
        -SignaturePath $manifestSignaturePath `
        -PublicKeyPath $publicKeyPath
    if ($LASTEXITCODE -ne 0) {
        throw "Update signature test failed with exit code $LASTEXITCODE."
    }

    $completed = $true
    Write-Host "Prepared release $nextVersion"
    foreach ($project in $projects) {
        $data = $releaseData[$project.ManifestKey]
        Write-Host "$($project.Name): $($data.Sha256) ($($data.Size) bytes)"
    }
    Write-Host 'Update the README patch notes, then review and test before committing or pushing.'
}
finally {
    if (!$completed) {
        foreach ($project in $projects) {
            Restore-File $project.AssemblyInfo
            Restore-File $project.ReleaseExe
        }
        Restore-File $manifestPath
        if (!$manifestExisted -and (Test-Path -LiteralPath $manifestPath)) {
            Remove-Item -LiteralPath $manifestPath -Force
        }
        Restore-File $manifestSignaturePath
        if (!$signatureExisted -and (Test-Path -LiteralPath $manifestSignaturePath)) {
            Remove-Item -LiteralPath $manifestSignaturePath -Force
        }
        Remove-NewNumberedArtifacts
    }

    $safeTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedBackupDirectory = [IO.Path]::GetFullPath($backupDirectory)
    if (!$resolvedBackupDirectory.StartsWith($safeTemporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedBackupDirectory) -notmatch '^GyeolheePinballRelease-[a-f0-9]{32}$') {
        throw 'Unsafe release backup cleanup path.'
    }
    Remove-Item -LiteralPath $resolvedBackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
