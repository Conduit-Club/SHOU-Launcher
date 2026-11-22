[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [ValidateSet('win-x64','win-arm64','osx-arm64')][string]$RuntimeIdentifier = '',
    [ValidateSet('Beta', 'Release', 'CI')][string]$Configuration = 'Beta',
    [string]$BuildSuffix = '',
    [string]$ReleaseTag = '',
    [switch]$Prerelease
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$metadata = Get-Content -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/metadata.json') -Raw | ConvertFrom-Json
$version = [string]$metadata.version.base
$releaseSource = Get-Content -LiteralPath (Join-Path $repo 'PCL.Core/Conduit/ClubRelease.cs') -Raw
$declaredVersion = [regex]::Match($releaseSource, 'CurrentVersion = "([^"]+)"').Groups[1].Value
$codename = [regex]::Match($releaseSource, 'CurrentCodename = "([^"]+)"').Groups[1].Value
if ($metadata.PSObject.Properties['codename'] -and $metadata.codename -cne $codename) { throw 'metadata codename and ClubRelease.CurrentCodename must match.' }
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne $declaredVersion) {
    throw 'metadata.json and ClubRelease.CurrentVersion must contain the same numeric version.'
}
$commit = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
if ($ReleaseTag) {
    $numeric=[version]$version
    $shortTag="v$($numeric.Major).$($numeric.Minor)"
    $validTag=if($Prerelease){
        $ReleaseTag -ceq "v$version" -or ($numeric.Build -eq 0 -and $ReleaseTag -ceq $shortTag) -or
            $ReleaseTag -cmatch ('^v'+[regex]::Escape($version)+'-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*$')
    }else{$ReleaseTag -ceq "v$version"}
    if ($Configuration -ne 'Release' -or -not $validTag -or $BuildSuffix) {
        throw 'A release package requires Release configuration, a matching version tag and no build suffix; short/suffixed tags require explicit prerelease mode.'
    }
    $tagCommit = git -C $repo rev-parse --verify "$ReleaseTag^{commit}"
    if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $commit) { throw 'Release tag does not point to the checked-out source.' }
    if (-not $Prerelease) {
        git -C $repo merge-base --is-ancestor HEAD origin/main
        if ($LASTEXITCODE -ne 0) { throw 'A formal release must be based on origin/main.' }
    }
} elseif (-not $BuildSuffix) {
    $channel = if ($Configuration -eq 'CI') { 'ci' } else { 'dev' }
    $run = if ($env:GITHUB_RUN_NUMBER) { "$($env:GITHUB_RUN_NUMBER)." } else { '' }
    $BuildSuffix = "$channel.$($run)g$($commit.Substring(0, 8))"
    if ($env:GITHUB_RUN_ATTEMPT -and $env:GITHUB_RUN_ATTEMPT -ne '1') { $BuildSuffix += ".r$env:GITHUB_RUN_ATTEMPT" }
}
if ($BuildSuffix -and $BuildSuffix -notmatch '^[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*$') { throw 'Invalid build suffix.' }
$label = if($ReleaseTag){$ReleaseTag}else{"v$version" + $(if ($BuildSuffix) { "-$BuildSuffix" } else { '' })}
[pscustomobject]@{
    Version = $version
    Codename = $codename
    Commit = $commit
    Label = $label
    Prerelease = [bool]$Prerelease
    ReleaseTag = $ReleaseTag
    ArchiveStem = "SHOU-Launcher-$(if ($RuntimeIdentifier) { $RuntimeIdentifier } else { 'win-' + $Architecture.ToLowerInvariant() })-$label"
}
