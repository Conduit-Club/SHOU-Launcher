function Resolve-ShouMacSigning {
    param([string]$Identity=$env:SHOU_MACOS_SIGNING_IDENTITY,
          [string]$Keychain=$env:SHOU_MACOS_SIGNING_KEYCHAIN,
          [switch]$AllowAdHoc)
    if (-not $Identity -and $env:GITHUB_ACTIONS -ne 'true') {
        $configPath=Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Library/Application Support/SHOU-Launcher-Signing/signing.json'
        if (Test-Path -LiteralPath $configPath) {
            $config=Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
            $Identity=$config.identity
            if (-not $Keychain) { $Keychain=$config.keychain }
            if ($Keychain -eq $config.keychain) {
                $passwordPath=Join-Path (Split-Path $configPath) 'password.txt'
                $password=[IO.File]::ReadAllText($passwordPath).Trim()
                try {
                    $null=& /usr/bin/security unlock-keychain -p $password $Keychain 2>&1
                    if ($LASTEXITCODE -ne 0) { throw 'Cannot unlock the local signing keychain.' }
                } finally { $password=$null }
            }
        }
    }
    if (-not $Identity -or $Identity -eq '-') {
        if (-not $AllowAdHoc -and $env:SHOU_MACOS_ALLOW_ADHOC -ne '1') {
            throw 'No fixed Mac signing identity. Run python3 scripts/macos/manage_signing.py init, or explicitly allow an ad-hoc PR/test package.'
        }
        return @{identity='-';keychain='';mode='ad-hoc test package; not notarized';arguments=@('--timestamp=none')}
    }
    if ($Identity -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Mac signing identity must be an exact certificate SHA-1 fingerprint.' }
    if (-not $Keychain -or -not (Test-Path -LiteralPath $Keychain -PathType Leaf)) { throw 'Explicit Mac signing keychain is missing.' }
    return @{identity=$Identity.ToUpperInvariant();keychain=$Keychain;mode='fixed certificate; not notarized';arguments=@('--keychain',$Keychain,'--timestamp=none')}
}

function Get-ShouMacKeychainSearchList {
    $result=& /usr/bin/security list-keychains -d user
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the user keychain search list.' }
    return @($result | ForEach-Object { $_.Trim().Trim('"') })
}

function Set-ShouMacKeychainSearchList([string[]]$Paths) {
    $null=& /usr/bin/security list-keychains -d user -s @Paths 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Cannot set the user keychain search list.' }
}
