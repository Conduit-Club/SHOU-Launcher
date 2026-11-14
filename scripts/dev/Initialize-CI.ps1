[CmdletBinding()]
param([Parameter(Mandatory)][string]$TaskRoot)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'CI bootstrap is only for an ephemeral GitHub runner; local jobs retain control/dev.py.'}
$root=[IO.Path]::GetFullPath($TaskRoot)
if(Test-Path -LiteralPath $root){throw 'CI task root must be new; refusing to replace an existing controller or tools registry.'}
$null=New-Item -ItemType Directory -Path (Join-Path $root 'control') -Force
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
$pwsh=(Get-Command pwsh -ErrorAction Stop).Source
$python=Get-Command python3 -ErrorAction SilentlyContinue
if(-not $python){$python=Get-Command python -ErrorAction Stop}
$version=(& $dotnet --version).Trim()
if($LASTEXITCODE -ne 0 -or $version -ne '10.0.403'){throw 'CI SDK must match global.json (10.0.403).'}
if(-not $env:JAVA_HOME){throw 'CI requires the explicitly selected native Temurin 21 JDK.'}
$javaTarget=[IO.Path]::GetFullPath($env:JAVA_HOME)
$suffix=if($IsWindows){'.exe'}else{''}
foreach($tool in @('java','javac','jar','jcmd')){
    if(-not (Test-Path -LiteralPath (Join-Path $javaTarget ('bin/'+$tool+$suffix)) -PathType Leaf)){throw "Selected CI JDK lacks $tool."}
}
$properties=(& (Join-Path $javaTarget ('bin/java'+$suffix)) '-XX:-UsePerfData' '-XshowSettings:properties' '-version' 2>&1) -join "`n"
if($LASTEXITCODE -ne 0 -or $properties -notmatch '(?m)^\s*java\.version\s*=\s*(21\.[^\s]+)\s*$'){throw 'Selected CI Java must be a working Java 21 JDK.'}
$javaVersion=$Matches[1]
if($properties -notmatch '(?m)^\s*os\.arch\s*=\s*([^\s]+)\s*$'){throw 'Cannot identify selected CI JDK architecture.'}
$javaArch=$Matches[1]
$hostArch=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if(($hostArch -eq 'Arm64' -and $javaArch -notin @('aarch64','arm64')) -or
   ($hostArch -eq 'X64' -and $javaArch -notin @('amd64','x86_64','x64')) -or $hostArch -notin @('Arm64','X64')){throw 'Selected CI JDK must match the runner native architecture.'}
# Only an ephemeral task alias is created. The hosted image JDK and global configuration stay intact.
$javaDirectory=Join-Path $root 'tools/java';$null=New-Item -ItemType Directory -Path $javaDirectory -Force
$javaHome=Join-Path $javaDirectory 'selected'
$linkType=if($IsWindows){'Junction'}else{'SymbolicLink'}
$null=New-Item -ItemType $linkType -Path $javaHome -Target $javaTarget
@{dotnet=@{path=(Split-Path $dotnet);version=$version};pwsh=@{path=(Split-Path $pwsh);version=$PSVersionTable.PSVersion.ToString()};python=@{executable=$python.Source};
    java=@{home=$javaHome;path=(Join-Path $javaHome 'bin');target=$javaTarget;version=$javaVersion;architecture=$javaArch;source='actions/setup-java@v5 Temurin'}} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'control/tools.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ci_gate.py') -Destination (Join-Path $root 'control/dev.py')
@{profile='ephemeral-github-standard';reserve_gib=8;stop_free_gib=10;task_budget_gib=35;
    note='Explicit ephemeral-runner profile; local night controller stays at 20 GiB reserve. Actual free space still determines admission.'} |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $root 'control/resource-policy.json') -Encoding utf8
if($env:GITHUB_ENV){"SHOU_DEV_ROOT=$root" | Add-Content -LiteralPath $env:GITHUB_ENV}
Write-Host 'Registered ephemeral runner tools and the same Publish/Validate entry chain; no global environment or identity changed.'
Write-Host 'CI-only resource profile: reserve 8 GiB, stop new work below 10 GiB free; local night policy is unchanged.'
