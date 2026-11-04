[CmdletBinding()]
param([ValidateSet('main','ui','platform')][string]$Worker='main',
    [Parameter(Mandatory)][string]$RunId,
    [ValidateSet('Publish','Build','Test')][string]$Action='Build',
    [ValidateSet('osx-arm64','win-x64','win-arm64')][string]$RuntimeIdentifier='osx-arm64',
    [ValidateSet('Debug','CI','Beta','Release')][string]$Configuration='Release',
    [string]$EntryProject='src/SHOU.Launcher.Desktop/SHOU.Launcher.Desktop.csproj',
    [string]$EnvFile='.env',[string]$Case='default',
    [string]$TaskRoot=$env:SHOU_DEV_ROOT,[string]$Python='/Users/moear/.pixi/bin/python3',[double]$PeakGiB=3)
$ErrorActionPreference='Stop'
foreach ($id in @($RunId,$Case)) { if ($id -notmatch '^[A-Za-z0-9_.-]+$' -or $id -in @('.','..')) { throw 'Invalid job/case identifier.' } }
if (-not $TaskRoot) { throw 'Explicit TaskRoot or SHOU_DEV_ROOT required.' }
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$tools=Get-Content -Raw -LiteralPath (Join-Path $TaskRoot 'control/tools.json') | ConvertFrom-Json
$dotnet=Join-Path $tools.dotnet.path $(if ($IsWindows) {'dotnet.exe'} else {'dotnet'})
$pwsh=Join-Path $tools.pwsh.path $(if ($IsWindows) {'pwsh.exe'} else {'pwsh'})
$build=Join-Path $TaskRoot "build/$Worker/$RunId/Desktop/$Configuration/$RuntimeIdentifier/$Case"
$output=Join-Path $TaskRoot "dist/$RunId/Desktop/$Configuration/$RuntimeIdentifier/$Case"
$command=@($pwsh,'-NoProfile','-File')
if ($Action -eq 'Publish') {
    $command+=@((Join-Path $repo 'scripts/conduit/Publish-Desktop.ps1'),'-RuntimeIdentifier',$RuntimeIdentifier,'-Configuration',$Configuration,'-Dotnet',$dotnet,'-EnvFile',$EnvFile,'-BuildRoot',$build,'-OutputRoot',$output)
} else {
    $command+=@((Join-Path $repo 'scripts/dev/Validate-Desktop.ps1'),'-Action',$Action,'-EntryProject',$EntryProject,'-RuntimeIdentifier',$RuntimeIdentifier,'-Configuration',$Configuration,'-Dotnet',$dotnet,'-BuildRoot',$build)
}
& $Python (Join-Path $TaskRoot 'control/dev.py') --worker $Worker --job "$RunId-$Action-$RuntimeIdentifier-$Case" --peak-gib $PeakGiB --cwd $repo -- @command
if ($LASTEXITCODE -ne 0) { throw "Guarded $Action failed: $LASTEXITCODE" }
