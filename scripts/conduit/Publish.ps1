[CmdletBinding()]
param(
    [ValidateSet('x64','ARM64')][string]$Architecture='x64',
    [ValidateSet('Beta','Release','CI')][string]$Configuration='Beta',
    [string]$Dotnet='dotnet',[string]$OutputRoot='artifacts/conduit',[string]$EnvFile='.env',
    [string]$BuildSuffix='',[string]$ReleaseTag='',[string]$BuildRoot=''
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'Build-Shared.ps1')
if ($env:SHOU_DEV_ROOT) { Assert-ConduitHeavyGate }
$outputBase=if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repo $OutputRoot }
$output=Join-Path $outputBase "win-$($Architecture.ToLowerInvariant())"
if ($env:SHOU_DEV_ROOT) { $null=Assert-ConduitTaskPath $output }
if (Test-Path -LiteralPath $output) { throw 'Legacy output already exists; choose a new output root.' }
$identity=& (Join-Path $PSScriptRoot 'Get-BuildIdentity.ps1') -Architecture $Architecture -Configuration $Configuration -BuildSuffix $BuildSuffix -ReleaseTag $ReleaseTag
$entry=Join-Path $repo 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj'
$props=@("-p:Platform=$Architecture","-p:Configuration=$Configuration","-p:RuntimeIdentifier=win-$($Architecture.ToLowerInvariant())",'-p:UseSharedCompilation=false')
if ($BuildRoot) {
    $BuildRoot=Assert-ConduitTaskPath $BuildRoot
    $props+=@('-p:UseArtifactsOutput=true',"-p:ArtifactsPath=$BuildRoot")
}
# The SDK removes RuntimeIdentifier from legacy library/analyzer ProjectReferences.
# Evaluate the context actually built, rather than a nominal entry-RID library path.
$referenceProps=@($props | Where-Object { -not $_.StartsWith('-p:RuntimeIdentifier=',[StringComparison]::OrdinalIgnoreCase) })+@('-p:RuntimeIdentifier=')
$null=New-Item -ItemType Directory -Path $output -Force
Push-Location $repo
try {
    Invoke-ConduitBuildEnvironment -Repo $repo -EnvFile $EnvFile -Action {
        param($inputConfig)
        if ($BuildRoot) {
            $reports=Join-Path $BuildRoot 'reports'
            $null=New-Item -ItemType Directory -Path $reports -Force
            Assert-ConduitProjectPaths $Dotnet @($entry) $props (Join-Path $reports 'paths-entry.json')
            Assert-ConduitProjectPaths $Dotnet @((Join-Path $repo 'PCL.Core/PCL.Core.csproj'),(Join-Path $repo 'PCL.Core.SourceGenerators/PCL.Core.SourceGenerators.csproj')) $referenceProps (Join-Path $reports 'paths-references.json')
        } else { $reports=$output }
        Invoke-ConduitDotnet $Dotnet (@('restore',$entry) + $props) (Join-Path $reports 'restore.log')
        Invoke-ConduitDotnet $Dotnet (@('clean',$entry,'-v','quiet') + $props) (Join-Path $reports 'clean.log')
        Invoke-ConduitDotnet $Dotnet (@('publish',$entry,'--no-restore','--self-contained','true','-o',$output) + $props) (Join-Path $reports 'publish.log')
        $ownerJson=& $Dotnet msbuild (Join-Path $repo 'PCL.Core/PCL.Core.csproj') '-getProperty:TargetPath,RuntimeIdentifier' @referenceProps
        if($LASTEXITCODE -ne 0){throw 'Cannot evaluate the actual legacy configuration owner.'}
        $owner=($ownerJson -join "`n" | ConvertFrom-Json).Properties.TargetPath
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $owner)) { throw 'Cannot locate the actual legacy configuration owner.' }
        $check=& (Join-Path $PSScriptRoot 'Assert-ClientCredentials.ps1') -AssemblyPath $owner
        $executable=Join-Path $output 'Plain Craft Launcher 2.exe'
        $bundledCheck=& (Join-Path $PSScriptRoot 'Assert-ClientCredentials.ps1') -AssemblyPath $executable
        $name="SHOU-Launcher-win-$($Architecture.ToLowerInvariant())"
        Move-Item -LiteralPath $executable -Destination (Join-Path $output ($name+'.exe'))
        Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'Plain Craft Launcher 2/LICENCE') -Destination $output
        Copy-Item -LiteralPath (Join-Path $repo 'docs/conduit/BUILD.md') -Destination (Join-Path $output 'BUILD.md')
        $info=[ordered]@{entry='Legacy WPF';version=$identity.Version;codename=$identity.Codename;label=$identity.Label;source_sha=$env:PCL_GITHUB_SHA;
            source_dirty=(-not [string]::IsNullOrWhiteSpace((& git -C $repo status --porcelain) -join "`n"));sdk=(& $Dotnet --version);
            configuration=$Configuration;runtime="win-$($Architecture.ToLowerInvariant())";self_contained=$true;
            configuration_owner=$owner;configuration_sources=$inputConfig.Sources;credential_verification=$check;bundled_owner_verification=$bundledCheck}
        $info | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'BUILD-INFO.json') -Encoding utf8
        "Version: $($identity.Version)`nBuild label: $($identity.Label)`nSource base commit: $env:PCL_GITHUB_SHA`nSource dirty: $($info.source_dirty)`nSDK: $($info.sdk)`nConfiguration: $Configuration`nRuntime: $($info.runtime)`nMicrosoft public Client ID configured: $($check.MS_CLIENT_IDConfigured)`nCurseForge API key embedding verified: $($check.CURSEFORGE_API_KEYEmbedded)" | Set-Content -LiteralPath (Join-Path $output 'BUILD-INFO.txt') -Encoding utf8
        $archive=Join-Path $outputBase ($identity.ArchiveStem+'.zip')
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive
        $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath ($archive+'.sha256') -Encoding ascii
        if ($env:GITHUB_OUTPUT) { "artifact_name=$($identity.ArchiveStem)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT; "version=$($identity.Version)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
        Write-Host "Legacy candidate completed: $archive"
    }
} finally { Pop-Location }
