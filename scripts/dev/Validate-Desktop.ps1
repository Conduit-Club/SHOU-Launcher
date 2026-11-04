[CmdletBinding()]
param([ValidateSet('Build','Test')][string]$Action='Build',[Parameter(Mandatory)][string]$EntryProject,
    [string]$RuntimeIdentifier='osx-arm64',[string]$Configuration='Release',[string]$Dotnet='dotnet',[Parameter(Mandatory)][string]$BuildRoot,[string]$TestFilter='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../conduit/Build-Shared.ps1')
Assert-ConduitHeavyGate
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$build=Assert-ConduitTaskPath $BuildRoot
$entry=Join-Path $repo $EntryProject
$report=Join-Path $build 'reports'
$null=New-Item -ItemType Directory -Path $report -Force
$props=@('-p:UseArtifactsOutput=true',"-p:ArtifactsPath=$build","-p:Configuration=$Configuration","-p:RuntimeIdentifier=$RuntimeIdentifier",'-p:UseSharedCompilation=false')
$projects=@($entry,(Join-Path $repo 'src/SHOU.Launcher.Core/SHOU.Launcher.Core.csproj'),(Join-Path $repo 'src/SHOU.Launcher.Desktop/SHOU.Launcher.Desktop.csproj'),(Join-Path $repo 'src/SHOU.Launcher.Generators/SHOU.Launcher.Generators.csproj')) | Select-Object -Unique
Assert-ConduitProjectPaths $Dotnet $projects $props (Join-Path $report 'paths.json')
Invoke-ConduitDotnet $Dotnet (@('restore',$entry) + $props) (Join-Path $report 'restore.log')
Invoke-ConduitDotnet $Dotnet (@('build',$entry,'--no-restore') + $props) (Join-Path $report 'build.log')
if ($Action -eq 'Test') {
    $arguments=@('test',$entry,'--no-restore','--no-build','--logger','trx;LogFileName=tests.trx','--results-directory',$report)
    if($TestFilter){$arguments+=@('--filter',$TestFilter)}
    Invoke-ConduitDotnet $Dotnet ($arguments + $props) (Join-Path $report 'test.log')
}
