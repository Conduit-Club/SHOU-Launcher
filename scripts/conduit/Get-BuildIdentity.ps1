[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [ValidateSet('Beta', 'Release', 'CI')][string]$Configuration = 'Beta',
    [string]$BuildSuffix = '',
    [string]$ReleaseTag = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$metadata = Get-Content -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/metadata.json') -Raw | ConvertFrom-Json
$version = [string]$metadata.version.base
$releaseSource = Get-Content -LiteralPath (Join-Path $repo 'PCL.Core/Conduit/ClubRelease.cs') -Raw
$declaredVersion = [regex]::Match($releaseSource, 'CurrentVersion = "([^"]+)"').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne $declaredVersion) {
    throw 'metadata.json and ClubRelease.CurrentVersion must contain the same numeric version.'
}
$commit = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
if ($ReleaseTag) {
    if ($Configuration -ne 'Release' -or $ReleaseTag -cne "v$version" -or $BuildSuffix) {
        throw 'A formal package requires Release configuration, an exact version tag, and no build suffix.'
    }
    $tagCommit = git -C $repo rev-parse --verify "$ReleaseTag^{commit}"
    if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $commit) { throw 'Release tag does not point to the checked-out source.' }
    git -C $repo merge-base --is-ancestor HEAD origin/main
    if ($LASTEXITCODE -ne 0) { throw 'A formal release must be based on origin/main.' }
} elseif (-not $BuildSuffix) {
    $channel = if ($Configuration -eq 'CI') { 'ci' } else { 'dev' }
    $run = if ($env:GITHUB_RUN_NUMBER) { "$($env:GITHUB_RUN_NUMBER)." } else { '' }
    $BuildSuffix = "$channel.$($run)g$($commit.Substring(0, 8))"
    if ($env:GITHUB_RUN_ATTEMPT -and $env:GITHUB_RUN_ATTEMPT -ne '1') { $BuildSuffix += ".r$env:GITHUB_RUN_ATTEMPT" }
}
if ($BuildSuffix -and $BuildSuffix -notmatch '^[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*$') { throw 'Invalid build suffix.' }
$label = "v$version" + $(if ($BuildSuffix) { "-$BuildSuffix" } else { '' })
[pscustomobject]@{
    Version = $version
    Commit = $commit
    Label = $label
    ArchiveStem = "SHOU-Launcher-win-$($Architecture.ToLowerInvariant())-$label"
}
