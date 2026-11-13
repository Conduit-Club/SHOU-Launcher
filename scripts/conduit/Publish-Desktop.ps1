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
$onefile = $RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)
$props = @('-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$build", "-p:Configuration=$Configuration", "-p:RuntimeIdentifier=$RuntimeIdentifier", '-p:SelfContained=true', '-p:UseSharedCompilation=false', "-p:PublishSingleFile=$($onefile.ToString().ToLowerInvariant())", "-p:IncludeNativeLibrariesForSelfExtract=$($onefile.ToString().ToLowerInvariant())", '-p:PublishTrimmed=false','-p:PublishAot=false','-p:UseAppHost=true')
if ($onefile) { $props += @('-p:DebugType=embedded', '-p:EnableCompressionInSingleFile=false', '-p:EnableSingleFileAnalyzer=false') }
$projects = @($entry, (Join-Path $repo 'src/SHOU.Launcher.Core/SHOU.Launcher.Core.csproj'), (Join-Path $repo 'src/SHOU.Launcher.Generators/SHOU.Launcher.Generators.csproj'))
$bootstrap=New-ConduitJavaBootstrap $repo $build
$props+=@("-p:JavaBootstrapJarPath=$($bootstrap.path)","-p:JavaBootstrapSha256=$($bootstrap.sha256)")
Push-Location $repo
try {
    Invoke-ConduitBuildEnvironment -Repo $repo -EnvFile $EnvFile -Action {
        param($inputConfig)
        $publishProps = @($props)
        Assert-ConduitProjectPaths -Dotnet $Dotnet -Projects $projects -Properties $props -ReportPath (Join-Path $report 'paths-before.json')
        Invoke-ConduitDotnet $Dotnet (@('restore',$entry) + $props) (Join-Path $report 'restore.log')
        Invoke-ConduitDotnet $Dotnet (@('clean',$entry,'-v','quiet') + $props) (Join-Path $report 'clean.log')
        if ($onefile) {
            # Gather notices from the exact restored runtime dependencies, then embed them.
            # Assets/localizations/bootstrap already use assembly resources; no loose payload is required.
            Invoke-ConduitDotnet $Dotnet (@('build',$entry,'--no-restore') + $props) (Join-Path $report 'documents-build.log')
            $binaries = @(Get-ChildItem -LiteralPath (Join-Path $build 'bin/SHOU.Launcher.Desktop') -Filter 'SHOU.Launcher.Desktop.deps.json' -Recurse -File)
            if ($binaries.Count -ne 1) { throw 'Expected one exact runtime dependency manifest.' }
            $binary = $binaries[0]
            $documents=Join-Path $build ('desktop-documents-' + [guid]::NewGuid().ToString('N'))
            $null=New-Item -ItemType Directory -Path $documents -Force
            Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'Plain Craft Launcher 2/LICENCE') -Destination $documents
            Copy-Item -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/metadata.json') -Destination (Join-Path $documents 'THIRD-PARTY-METADATA.json')
            & (Join-Path $PSScriptRoot 'Collect-DesktopLicenses.ps1') -PublishDirectory $documents -DependencyFile $binary.FullName
            Copy-Item -LiteralPath (Join-Path $repo 'src/SHOU.Launcher.Desktop/Assets/AchievementIcons/LICENSE.txt') -Destination (Join-Path $documents 'licenses/mcgen.txt')
            Copy-Item -LiteralPath (Join-Path $repo 'src/SHOU.Launcher.Desktop/Assets/AchievementIcons/README.md') -Destination (Join-Path $documents 'licenses/achievement-icons.md')
            $documentArchive=$documents + '.zip'
            Compress-Archive -Path (Join-Path $documents '*') -DestinationPath $documentArchive
            $publishProps = $publishProps + @("-p:DesktopDocumentsArchive=$documentArchive")
        }
        Assert-ConduitProjectPaths -Dotnet $Dotnet -Projects $projects -Properties $publishProps -ReportPath (Join-Path $report 'paths-before-publish.json')
        Invoke-ConduitDotnet $Dotnet (@('publish',$entry,'--no-restore','-o',(Join-Path $build 'publish-desktop')) + $publishProps) (Join-Path $report 'publish.log')
        Assert-ConduitProjectPaths -Dotnet $Dotnet -Projects $projects -Properties $publishProps -ReportPath (Join-Path $report 'paths-after.json')
        $publish = Join-Path $build 'publish-desktop'
        $owner = if ($onefile) { Join-Path $publish 'SHOU.Launcher.Desktop.exe' } else { Join-Path $publish 'SHOU.Launcher.Core.dll' }
        if ($onefile) {
            $coreOwner=Join-Path $binary.Directory.FullName 'SHOU.Launcher.Core.dll'
            & (Join-Path $PSScriptRoot 'Assert-DesktopBundle.ps1') -Executable $owner -Dotnet $Dotnet -CoreAssembly $coreOwner -RuntimeIdentifier $RuntimeIdentifier -ReportPath (Join-Path $report 'bundle.json')
        }
        $check = & (Join-Path $PSScriptRoot 'Assert-ClientCredentials.ps1') -AssemblyPath $owner
        $dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repo status --porcelain) -join "`n")
        $info = [ordered]@{ entry='Avalonia Desktop'; version=$identity.Version; codename=$identity.Codename; label=$identity.Label; source_sha=$env:PCL_GITHUB_SHA; source_dirty=$dirty;
            sdk=(& $Dotnet --version); avalonia='12.1.5'; runtime=$RuntimeIdentifier; configuration=$Configuration; self_contained=$true;
            configuration_owner='SHOU.Launcher.Core.dll'; distribution=$(if($onefile){'single self-contained EXE; native runtime extraction'}else{'application bundle'}); configuration_sources=$inputConfig.Sources; credential_verification=$check;
            java_bootstrap=@{sha256=$bootstrap.sha256;source_sha256=$bootstrap.source_sha256;compiler=$bootstrap.compiler;target_class_version=52;transport='private stdin'};
            signing=$(if($RuntimeIdentifier -eq 'osx-arm64'){'ad-hoc local candidate; not notarized'}else{'unsigned local candidate'}); windows_default='legacy WPF'; built_at=[DateTimeOffset]::UtcNow.ToString('o') }
        if (-not $onefile) {
            Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'Plain Craft Launcher 2/LICENCE') -Destination $publish
            Copy-Item -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/metadata.json') -Destination (Join-Path $publish 'THIRD-PARTY-METADATA.json')
            & (Join-Path $PSScriptRoot 'Collect-DesktopLicenses.ps1') -PublishDirectory $publish
            $info | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publish 'BUILD-INFO.json') -Encoding utf8
        } elseif (@(Get-ChildItem -LiteralPath $publish -Force).Count -ne 1 -or -not (Test-Path -LiteralPath $owner -PathType Leaf)) { throw 'Windows distribution must contain exactly one self-contained EXE.' }
        if ($onefile) { $info.payload=@{name=[IO.Path]::GetFileName($owner);bytes=(Get-Item -LiteralPath $owner).Length;sha256=(Get-FileHash -LiteralPath $owner -Algorithm SHA256).Hash.ToLowerInvariant()} }
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
        $finalOwner = if ($RuntimeIdentifier -eq 'osx-arm64') { Join-Path $output 'SHOU Launcher.app/Contents/MacOS/SHOU.Launcher.Core.dll' } else { Join-Path $output 'SHOU Launcher/SHOU.Launcher.Desktop.exe' }
        if ((Get-FileHash $owner).Hash -ne (Get-FileHash $finalOwner).Hash) { throw 'Final package configuration owner mismatch.' }
        $null = & (Join-Path $PSScriptRoot 'Assert-ClientCredentials.ps1') -AssemblyPath $finalOwner
        Write-Host "Candidate completed: $archive"
    }
} finally { Pop-Location }
