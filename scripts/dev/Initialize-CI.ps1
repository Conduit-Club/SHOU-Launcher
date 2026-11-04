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
@{dotnet=@{path=(Split-Path $dotnet);version=$version};pwsh=@{path=(Split-Path $pwsh);version=$PSVersionTable.PSVersion.ToString()};python=@{executable=$python.Source}} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'control/tools.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ci_gate.py') -Destination (Join-Path $root 'control/dev.py')
@{profile='ephemeral-github-standard';reserve_gib=8;stop_free_gib=10;task_budget_gib=35;
    note='Explicit ephemeral-runner profile; local night controller stays at 20 GiB reserve. Actual free space still determines admission.'} |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $root 'control/resource-policy.json') -Encoding utf8
if($env:GITHUB_ENV){"SHOU_DEV_ROOT=$root" | Add-Content -LiteralPath $env:GITHUB_ENV}
Write-Host 'Registered ephemeral runner tools and the same Publish/Validate entry chain; no global environment or identity changed.'
Write-Host 'CI-only resource profile: reserve 8 GiB, stop new work below 10 GiB free; local night policy is unchanged.'
