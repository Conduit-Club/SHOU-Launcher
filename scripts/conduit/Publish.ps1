[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [ValidateSet('Beta', 'Release')][string]$Configuration = 'Beta',
    [string]$Dotnet = 'dotnet',
    [string]$OutputRoot = 'artifacts/conduit',
    [string]$EnvFile = '.env',
    [string]$BuildSuffix = '',
    [string]$ReleaseTag = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path (Join-Path $repo $OutputRoot) "win-$($Architecture.ToLowerInvariant())"
if (Test-Path $output) {
    throw "Output already exists: $output. Move it aside before publishing again."
}
# Read only the explicitly supported client credentials. Never evaluate .env as code.
. (Join-Path $PSScriptRoot 'BuildEnvironment.ps1')
$envPath = Join-Path $repo $EnvFile
$credentials = Read-ConduitBuildCredentials -EnvPath $envPath
$clientId = $credentials.MicrosoftClientId
$curseForgeApiKey = $credentials.CurseForgeApiKey
if ($clientId -and -not [guid]::TryParse($clientId, [ref]([guid]::Empty))) {
    throw 'PCL_MS_CLIENT_ID must be an application GUID.'
}
$savedPclEnvironment = @{}
$identity = & (Join-Path $PSScriptRoot 'Get-BuildIdentity.ps1') -Architecture $Architecture -Configuration $Configuration -BuildSuffix $BuildSuffix -ReleaseTag $ReleaseTag
Get-ChildItem Env:PCL_* | ForEach-Object { $savedPclEnvironment[$_.Name] = $_.Value }
Push-Location $repo
try {
    # Isolate the three approved client-build inputs and restore the caller's environment on exit.
    Get-ChildItem Env:PCL_* | ForEach-Object { [Environment]::SetEnvironmentVariable($_.Name, $null, 'Process') }
    $env:PCL_WRITE_SECRET = '1'
    $env:PCL_MS_CLIENT_ID = $clientId
    $env:PCL_CURSEFORGE_API_KEY = $curseForgeApiKey
    $env:PCL_GITHUB_SHA = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
    # A preceding build for another architecture may have replaced project.assets.json.
    # Restore this target before clean, which also resolves the runtime-specific assets.
    & $Dotnet restore 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' "-p:Platform=$Architecture" -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed: $LASTEXITCODE" }
    # Force regeneration when .env changes; compiler servers can retain stale environment values.
    & $Dotnet clean 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c $Configuration "-p:Platform=$Architecture" -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed: $LASTEXITCODE" }
    & $Dotnet publish 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c $Configuration "-p:Platform=$Architecture" -p:UseSharedCompilation=false --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    $coreAssembly = Join-Path $repo "PCL.Core/bin/$Configuration-$Architecture/net10.0-windows/PCL.Core.dll"
    $credentialCheck = & (Join-Path $PSScriptRoot 'Assert-ClientCredentials.ps1') -AssemblyPath $coreAssembly
    $name = "SHOU-Launcher-win-$($Architecture.ToLowerInvariant())"
    Move-Item -LiteralPath (Join-Path $output 'Plain Craft Launcher 2.exe') -Destination (Join-Path $output "$name.exe")
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/LICENCE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'docs/conduit/BUILD.md') -Destination (Join-Path $output 'BUILD.md')
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
    $sdk = & $Dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve SDK version.' }
    $sourceState = if (git status --porcelain) { 'modified working tree (includes uncommitted changes)' } else { 'clean' }
    $appConfigured = -not [string]::IsNullOrWhiteSpace($clientId)
    $metadata = Get-Content -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/metadata.json') -Raw | ConvertFrom-Json
    $releaseSource = Get-Content -LiteralPath (Join-Path $repo 'PCL.Core/Conduit/ClubRelease.cs') -Raw
    $codename = [regex]::Match($releaseSource, 'CurrentCodename = "([^"]+)"').Groups[1].Value
    "Version: $($metadata.version.base)`nBuild label: $($identity.Label)`nCodename: $codename`nSource base commit: $commit`nSource state: $sourceState`nSDK: $sdk`nConfiguration: $Configuration`nRuntime: win-$($Architecture.ToLowerInvariant())`nSelf-contained: true`nMicrosoft public Client ID configured: $appConfigured`nCurseForge API key configured: $(-not [string]::IsNullOrWhiteSpace($curseForgeApiKey))`nCurseForge API key embedding verified: $($credentialCheck.CURSEFORGE_API_KEYEmbedded)`nOther service credentials: not embedded" | Set-Content (Join-Path $output 'BUILD-INFO.txt') -Encoding utf8
    $archiveName = "$($identity.ArchiveStem).zip"
    $archive = Join-Path (Split-Path $output) $archiveName
    Compress-Archive -Path "$output/*" -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $archiveName" | Set-Content "$archive.sha256" -Encoding ascii
    if ($env:GITHUB_OUTPUT) {
        "artifact_name=$($identity.ArchiveStem)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
        "version=$($identity.Version)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    }
    Write-Host "Published: $archive"
} finally {
    Get-ChildItem Env:PCL_* | ForEach-Object { [Environment]::SetEnvironmentVariable($_.Name, $null, 'Process') }
    foreach ($name in $savedPclEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedPclEnvironment[$name], 'Process')
    }
    Pop-Location
}
