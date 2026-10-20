[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path $repo "artifacts/conduit/win-$($Architecture.ToLowerInvariant())"
if (Test-Path $output) {
    throw "Output already exists: $output. Move it aside before publishing again."
}
if ($env:PCL_WRITE_SECRET) {
    throw 'Baseline builds must not embed PCL_* environment variables. Unset PCL_WRITE_SECRET first.'
}
Push-Location $repo
try {
    & $Dotnet publish 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c Beta "-p:Platform=$Architecture" --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    $name = "PCL-CE-SHOU-win-$($Architecture.ToLowerInvariant())"
    Move-Item -LiteralPath (Join-Path $output 'Plain Craft Launcher 2.exe') -Destination (Join-Path $output "$name.exe")
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/LICENCE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'docs/conduit/BUILD.md') -Destination (Join-Path $output 'BUILD.md')
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
    $sdk = & $Dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve SDK version.' }
    "Source: $commit`nSDK: $sdk`nConfiguration: Beta`nRuntime: win-$($Architecture.ToLowerInvariant())`nSelf-contained: true`nService credentials: not embedded" | Set-Content (Join-Path $output 'BUILD-INFO.txt') -Encoding utf8
    $archive = Join-Path (Split-Path $output) "$name.zip"
    Compress-Archive -Path "$output/*" -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content "$archive.sha256" -Encoding ascii
    Write-Host "Published: $archive"
} finally {
    Pop-Location
}
