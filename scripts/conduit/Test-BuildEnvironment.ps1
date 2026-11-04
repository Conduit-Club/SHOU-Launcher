[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BuildEnvironment.ps1')
$testFile = Join-Path ([IO.Path]::GetTempPath()) ('shou-build-env-' + [guid]::NewGuid().ToString('N') + '.env')
$names = @('PCL_MS_CLIENT_ID', 'PCL_CURSEFORGE_API_KEY', 'CURSEFORGE_API_KEY', 'CURSEFORGE_API')
$saved = @{}
function Assert-BuildEnvironment([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
try {
    foreach ($name in $names) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    foreach ($alias in @('PCL_CURSEFORGE_API_KEY', 'CURSEFORGE_API_KEY', 'CURSEFORGE_API')) {
        foreach ($quote in @('', '"', "'")) {
            ($alias + '=' + $quote + '$2a$literal-key' + $quote) | Set-Content -LiteralPath $testFile
            $result = Read-ConduitBuildCredentials -EnvPath $testFile
            Assert-BuildEnvironment ($result.CurseForgeApiKey -ceq '$2a$literal-key') 'Key alias/quoting changed a literal value.'
        }
    }
    @('CURSEFORGE_API=alias', 'PCL_CURSEFORGE_API_KEY=canonical') | Set-Content -LiteralPath $testFile
    $result = Read-ConduitBuildCredentials -EnvPath $testFile
    Assert-BuildEnvironment ($result.CurseForgeApiKey -ceq 'canonical') 'Canonical file key must win regardless of assignment order.'
    $env:CURSEFORGE_API_KEY = 'process-value'
    $result = Read-ConduitBuildCredentials -EnvPath $testFile
    Assert-BuildEnvironment ($result.CurseForgeApiKey -ceq 'process-value') 'Process environment must override file configuration.'
    $env:PCL_CURSEFORGE_API_KEY = 'canonical-process'
    $result = Read-ConduitBuildCredentials -EnvPath $testFile
    Assert-BuildEnvironment ($result.CurseForgeApiKey -ceq 'canonical-process') 'Canonical process key must win.'
    'UNSUPPORTED=private-value-do-not-print' | Set-Content -LiteralPath $testFile
    $rejected = $false
    try { $null = Read-ConduitBuildCredentials -EnvPath $testFile }
    catch {
        $rejected = $true
        Assert-BuildEnvironment (-not $_.Exception.Message.Contains('private-value-do-not-print')) 'Parser errors exposed a value.'
    }
    Assert-BuildEnvironment $rejected 'Unsupported assignments must be rejected.'
    Write-Output 'Build environment: 13 checks passed (literal keys, aliases, precedence, safe rejection).'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    if (Test-Path -LiteralPath $testFile) { Remove-Item -LiteralPath $testFile }
}
