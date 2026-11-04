[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('osx-arm64','win-x64','win-arm64')][string]$RuntimeIdentifier,
    [ValidateSet('Beta','Release','CI')][string]$Configuration = 'Beta',
    [string]$Dotnet = 'dotnet', [string]$EnvFile = '.env',
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$BuildSuffix = '', [string]$ReleaseTag = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Shared.ps1')
Assert-ConduitHeavyGate
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$build = Assert-ConduitTaskPath $BuildRoot
$output = Assert-ConduitTaskPath $OutputRoot
if (Test-Path -LiteralPath $output) { throw 'Candidate output already exists; use a new output root (the build root may be reused for B07).' }
$report = Assert-ConduitTaskPath (Join-Path $build 'reports')
$null = New-Item -ItemType Directory -Path $build,$output,$report -Force
$identity = & (Join-Path $PSScriptRoot 'Get-BuildIdentity.ps1') -RuntimeIdentifier $RuntimeIdentifier -Configuration $Configuration -BuildSuffix $BuildSuffix -ReleaseTag $ReleaseTag
$entry = Join-Path $repo 'src/SHOU.Launcher.Desktop/SHOU.Launcher.Desktop.csproj'
$props = @('-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$build", "-p:Configuration=$Configuration", "-p:RuntimeIdentifier=$RuntimeIdentifier", '-p:SelfContained=true', '-p:UseSharedCompilation=false', '-p:PublishSingleFile=false','-p:IncludeNativeLibrariesForSelfExtract=false','-p:PublishTrimmed=false','-p:PublishAot=false','-p:UseAppHost=true')
$projects = @($entry, (Join-Path $repo 'src/SHOU.Launcher.Core/SHOU.Launcher.Core.csproj'), (Join-Path $repo 'src/SHOU.Launcher.Generators/SHOU.Launcher.Generators.csproj'))
Push-Location $repo
try {
    Invoke-ConduitBuildEnvironment -Repo $repo -EnvFile $EnvFile -Action {
        param($inputConfig)
        Assert-ConduitProjectPaths -Dotnet $Dotnet -Projects $projects -Properties $props -ReportPath (Join-Path $report 'paths-before.json')
        Invoke-ConduitDotnet $Dotnet (@('restore',$entry) + $props) (Join-Path $report 'restore.log')
        Invoke-ConduitDotnet $Dotnet (@('clean',$entry,'-v','quiet') + $props) (Join-Path $report 'clean.log')
        Invoke-ConduitDotnet $Dotnet (@('publish',$entry,'--no-restore','-o',(Join-Path $build 'publish-desktop')) + $props) (Join-Path $report 'publish.log')
        Assert-ConduitProjectPaths -Dotnet $Dotnet -Projects $projects -Properties $props -ReportPath (Join-Path $report 'paths-after.json')
        $publish = Join-Path $build 'publish-desktop'
        $owner = Join-Path $publish 'SHOU.Launcher.Core.dll'
        $check = & (Join-Path $PSScriptRoot 'Assert-ClientCredentials.ps1') -AssemblyPath $owner
        $dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repo status --porcelain) -join "`n")
        $info = [ordered]@{ entry='Avalonia Desktop'; version=$identity.Version; codename=$identity.Codename; label=$identity.Label; source_sha=$env:PCL_GITHUB_SHA; source_dirty=$dirty;
            sdk=(& $Dotnet --version); avalonia='12.1.5'; runtime=$RuntimeIdentifier; configuration=$Configuration; self_contained=$true;
            configuration_owner='SHOU.Launcher.Core.dll'; configuration_sources=$inputConfig.Sources; credential_verification=$check;
            signing='local candidate; not notarized'; windows_default='legacy WPF'; built_at=[DateTimeOffset]::UtcNow.ToString('o') }
        Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'Plain Craft Launcher 2/LICENCE') -Destination $publish
        Copy-Item -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/metadata.json') -Destination (Join-Path $publish 'THIRD-PARTY-METADATA.json')
        & (Join-Path $PSScriptRoot 'Collect-DesktopLicenses.ps1') -PublishDirectory $publish
        $info | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publish 'BUILD-INFO.json') -Encoding utf8
        $info | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'BUILD-INFO.json') -Encoding utf8
        if ($RuntimeIdentifier -eq 'osx-arm64') {
            & (Join-Path $repo 'scripts/macos/Package-App.ps1') -PublishDirectory $publish -OutputDirectory $output -Version $identity.Version -ArchiveStem $identity.ArchiveStem
        } else {
            $destination = Join-Path $output 'SHOU Launcher'
            Copy-Item -LiteralPath $publish -Destination $destination -Recurse
            Compress-Archive -Path (Join-Path $destination '*') -DestinationPath (Join-Path $output ($identity.ArchiveStem + '.zip'))
        }
        $archive = Join-Path $output ($identity.ArchiveStem + '.zip')
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
        $finalOwner = if ($RuntimeIdentifier -eq 'osx-arm64') { Join-Path $output 'SHOU Launcher.app/Contents/MacOS/SHOU.Launcher.Core.dll' } else { Join-Path $output 'SHOU Launcher/SHOU.Launcher.Core.dll' }
        if ((Get-FileHash $owner).Hash -ne (Get-FileHash $finalOwner).Hash) { throw 'Final package configuration owner mismatch.' }
        $null = & (Join-Path $PSScriptRoot 'Assert-ClientCredentials.ps1') -AssemblyPath $finalOwner
        Write-Host "Candidate completed: $archive"
    }
} finally { Pop-Location }
