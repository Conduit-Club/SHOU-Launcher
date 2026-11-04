[CmdletBinding()]
param([Parameter(Mandatory)][string]$Dotnet,[Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$ReportRoot,[Parameter(Mandatory)][string]$FixtureOutputRoot,
    [ValidateSet('All','Inputs','Compiler','Sequence')][string]$Mode='All',
    [ValidateSet('win-x64','win-arm64','osx-arm64')][string]$SequenceRid='win-x64')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../conduit/Build-Shared.ps1')
Assert-ConduitHeavyGate
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$build=Assert-ConduitTaskPath $BuildRoot
$report=Assert-ConduitTaskPath $ReportRoot
$fixtures=Assert-ConduitTaskPath $FixtureOutputRoot
$null=New-Item -ItemType Directory -Path $build,$report,$fixtures -Force
$inputs=Join-Path $report 'inputs';$null=New-Item -ItemType Directory -Path $inputs -Force
$empty=Join-Path $inputs 'empty.input';[IO.File]::WriteAllText($empty,'')
$saved=@{}
foreach($name in (@((Get-ChildItem Env:PCL_*).Name)+@('CURSEFORGE_API_KEY','CURSEFORGE_API') | Select-Object -Unique)) {
    $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
}
$cases=[Collections.Generic.List[object]]::new()
$current='setup';$failure=$null
if(-not ('Conduit.BuildContractProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
namespace Conduit { public static class BuildContractProbe {
    public static bool Contains(byte[] bytes, string value) => ((ReadOnlySpan<byte>)bytes).IndexOf(Encoding.Unicode.GetBytes(value)) >= 0;
} }
'@
}
function Clear-Inputs {
    Get-ChildItem Env:PCL_* | ForEach-Object { Remove-Item -LiteralPath ('Env:'+$_.Name) -ErrorAction SilentlyContinue }
    foreach($name in @('CURSEFORGE_API_KEY','CURSEFORGE_API')){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}
}
function Require([bool]$Condition,[string]$Message) { if(-not $Condition){throw $Message} }
function Owner-Contains([string]$Path,[string]$Value) {
    $bytes=[IO.File]::ReadAllBytes($Path)
    return [Conduit.BuildContractProbe]::Contains($bytes,$Value)
}
function Read-Provider([string]$Owner) {
    # The CLR can share a mapped PE image for an identical file path across collectible
    # contexts. Probe an exact byte copy at a unique path, while all compiler/obj roots
    # remain fixed. These are verification copies, never fresh build intermediates.
    $probeDirectory=Join-Path $build ('provider-probes/'+[guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $probeDirectory
    $probe=Join-Path $probeDirectory 'SHOU.Launcher.Core.dll'
    Copy-Item -LiteralPath $Owner -Destination $probe
    Require ((Get-FileHash $Owner).Hash -eq (Get-FileHash $probe).Hash) 'Provider probe copy differs from actual owner.'
    $context=[Runtime.Loader.AssemblyLoadContext]::new('contract-'+[guid]::NewGuid(),$true)
    try {
        $assembly=$context.LoadFromAssemblyPath($probe)
        $type=$assembly.GetType('SHOU.Launcher.Core.Configuration.BuildConfiguration',$true)
        $instance=[Activator]::CreateInstance($type)
        return @{Microsoft=$type.GetProperty('MicrosoftClientId').GetValue($instance);CurseForge=$type.GetProperty('CurseForgeApiKey').GetValue($instance);SourceSha=$type.GetProperty('SourceSha').GetValue($instance);Configuration=$type.GetProperty('Configuration').GetValue($instance)}
    } finally {$context.Unload()}
}
function Build-Core([string]$Case,[string]$Configuration) {
    $root=Join-Path $build 'compiler' # switches reuse this same intermediate root.
    $logs=Join-Path $report "compiler/$Case";$null=New-Item -ItemType Directory -Path $logs -Force
    $entry=Join-Path $repo 'src/SHOU.Launcher.Core/SHOU.Launcher.Core.csproj'
    $props=@('-p:UseArtifactsOutput=true',"-p:ArtifactsPath=$root","-p:Configuration=$Configuration",'-p:UseSharedCompilation=false')
    Assert-ConduitProjectPaths $Dotnet @($entry,(Join-Path $repo 'src/SHOU.Launcher.Generators/SHOU.Launcher.Generators.csproj')) $props (Join-Path $logs 'paths.json')
    Invoke-ConduitDotnet $Dotnet (@('restore',$entry)+$props) (Join-Path $logs 'restore.log')
    Invoke-ConduitDotnet $Dotnet (@('clean',$entry,'-v','quiet')+$props) (Join-Path $logs 'clean.log')
    Invoke-ConduitDotnet $Dotnet (@('build',$entry,'--no-restore')+$props) (Join-Path $logs 'build.log')
    $owner=& $Dotnet msbuild $entry '-getProperty:TargetPath' @props
    if($LASTEXITCODE -ne 0){throw 'Cannot locate compiler-contract owner.'}
    return [string]$owner
}
try {
    Clear-Inputs
    $a='11111111-1111-4111-8111-111111111111';$b='22222222-2222-4222-8222-222222222222'
    $cfA='SHOU_FAKE_A_quote_"_slash_\_dollar_$';$cfB='SHOU_FAKE_B_quote_"_slash_\_dollar_$'
    $unlisted='SHOU_FAKE_UNLISTED_733654926'
    if($Mode -in @('All','Inputs')) {
        $current='B02-B04-B13-B14'
        $file=Join-Path $inputs 'precedence.input'
        [IO.File]::WriteAllText($file,"PCL_MS_CLIENT_ID='$a'`nPCL_CURSEFORGE_API_KEY='file-official'`nCURSEFORGE_API_KEY='file-alias'`n")
        $env:CURSEFORGE_API_KEY='process-alias'
        $inputConfig=Get-ConduitBuildInput $repo $file
        Require ($inputConfig.Credentials.CurseForgeApiKey -eq 'process-alias') 'B02 process alias lost to file official name.'
        $env:PCL_CURSEFORGE_API_KEY='process-official'
        Require ((Get-ConduitBuildInput $repo $file).Credentials.CurseForgeApiKey -eq 'process-official') 'B02 official process priority failed.'
        $env:PCL_CURSEFORGE_API_KEY=' ';$env:CURSEFORGE_API_KEY=' '
        Require ((Get-ConduitBuildInput $repo $file).Credentials.CurseForgeApiKey -eq 'file-official') 'B03 whitespace fallback failed.'
        [IO.File]::WriteAllText($file,"CURSEFORGE_API_KEY='first'`nCURSEFORGE_API_KEY='second'`n")
        Require ((Get-ConduitBuildInput $repo $file).Credentials.CurseForgeApiKey -eq 'second') 'B04 duplicate assignment semantics changed.'
        [IO.File]::AppendAllText($file,"UNKNOWN_LITERAL='never executed'`n")
        $rejected=$false
        try {$null=Get-ConduitBuildInput $repo $file} catch {$rejected=$_.Exception.Message -match '^Unsupported .env assignment at line 3'}
        Require $rejected 'B04 unknown assignment was not rejected safely.'
        Clear-Inputs
        $missing=Get-ConduitBuildInput $repo '.env'
        Require (-not $missing.Credentials.MicrosoftClientId -and -not $missing.Credentials.CurseForgeApiKey) 'B03 absent default file did not yield empty inputs.'
        $rejected=$false
        try{$null=Get-ConduitBuildInput $repo (Join-Path $inputs 'missing-explicit.input')}catch{$rejected=$_.Exception.Message -eq 'Explicit EnvFile is not readable.'}
        Require $rejected 'B03 missing explicit file was not rejected.'
        $env:PCL_SENTINEL='caller-sentinel';$env:PCL_GITHUB_SHA='caller-sha'
        foreach($throws in @($false,$true)) {
            $caught=$false
            try { Invoke-ConduitBuildEnvironment $repo $empty {if($throws){throw [InvalidOperationException]::new('fixture failure')}} }
            catch {$caught=$true;if(-not $throws){throw}}
            Require ($caught -eq $throws) 'B13 expected exception path did not execute.'
            Require ($env:PCL_SENTINEL -eq 'caller-sentinel' -and $env:PCL_GITHUB_SHA -eq 'caller-sha') 'B13 caller environment not restored.'
        }
        $cases.Add(@{id=$current;status='PASS';explicit_external_env_file=$true;success_and_exception_restore=$true})
        Clear-Inputs
    }
    if($Mode -in @('All','Compiler')) {
        foreach($switchValue in @('absent','1','0','false')) {
            $current='B05-B06-'+$switchValue;Clear-Inputs
            $env:PCL_MS_CLIENT_ID=$a;$env:PCL_CURSEFORGE_API_KEY=$cfA;$env:PCL_GITHUB_SHA='fixture-source';$env:PCL_UNLISTED_FAKE_TOKEN=$unlisted
            if($switchValue -ne 'absent'){$env:PCL_WRITE_SECRET=$switchValue}
            $owner=Build-Core $current 'Release'
            if($switchValue -ne 'absent') {$null=& (Join-Path $repo 'scripts/conduit/Assert-ClientCredentials.ps1') -AssemblyPath $owner -ErrorAction Stop}
            # The absent switch intentionally must not embed configured inputs; check without the positive verifier.
            $provider=Read-Provider $owner
            Require (($provider.Microsoft -eq $a) -eq ($switchValue -ne 'absent')) 'B05 switch-presence behavior changed.'
            Require (($provider.CurseForge -eq $cfA) -eq ($switchValue -ne 'absent')) 'B06 escaped input did not round-trip.'
            Require (-not (Owner-Contains $owner $unlisted)) 'B06 non-allowlisted input entered owner.'
            $cases.Add(@{id=$current;status='PASS';owner_sha256=(Get-FileHash $owner).Hash;configuration=$provider.Configuration})
        }
        foreach($configuration in @('Debug','CI','Beta','Release')) {
            $current='B11-'+$configuration;Clear-Inputs
            $owner=Build-Core $current $configuration
            $env:PCL_MS_CLIENT_ID=$b;$env:PCL_CURSEFORGE_API_KEY=$cfB;$env:PCL_GITHUB_SHA='hostile-runtime-sha'
            $provider=Read-Provider $owner
            Require (($provider.Microsoft -eq $b) -eq ($configuration -eq 'Debug')) 'B11 unexpected runtime fallback.'
            Require ($provider.SourceSha -eq 'unconfigured') 'B16 runtime environment changed source identity.'
            Require ($provider.Configuration -eq $configuration) 'B11 configuration symbol mismatch.'
            $cases.Add(@{id=$current;status='PASS';source_runtime_fallback=$false})
        }
        $current='B11-embedded-priority';Clear-Inputs
        $env:PCL_WRITE_SECRET='1';$env:PCL_MS_CLIENT_ID=$a;$env:PCL_CURSEFORGE_API_KEY=$cfA;$env:PCL_GITHUB_SHA='fixture-source'
        $owner=Build-Core $current 'Debug'
        $env:PCL_MS_CLIENT_ID=$b;$env:PCL_GITHUB_SHA='hostile-runtime-sha'
        $provider=Read-Provider $owner
        Require ($provider.Microsoft -eq $a -and $provider.SourceSha -eq 'fixture-source') 'B11 embedded Debug input lost priority.'
        $cases.Add(@{id=$current;status='PASS'})
    }
    if($Mode -in @('All','Sequence')) {
        $intermediate=Join-Path $build 'B07-Desktop'
        $sha=& git -C $repo rev-parse HEAD
        foreach($case in @('A','B','none')) {
            $current='B07-'+$case;Clear-Inputs
            $envFile=Join-Path $inputs ($case+'.input')
            $text=if($case -eq 'A'){"PCL_MS_CLIENT_ID='$a'`nPCL_CURSEFORGE_API_KEY='$cfA'`n"}elseif($case -eq 'B'){"PCL_MS_CLIENT_ID='$b'`nPCL_CURSEFORGE_API_KEY='$cfB'`n"}else{''}
            [IO.File]::WriteAllText($envFile,$text)
            $env:PCL_UNLISTED_FAKE_TOKEN=$unlisted
            $out=Join-Path $fixtures $case
            & (Join-Path $repo 'scripts/conduit/Publish-Desktop.ps1') -RuntimeIdentifier $SequenceRid -Configuration Release -Dotnet $Dotnet -EnvFile $envFile -BuildRoot $intermediate -OutputRoot $out -BuildSuffix ('B07-'+$case)
            $owner=if($SequenceRid -eq 'osx-arm64'){Join-Path $out 'SHOU Launcher.app/Contents/MacOS/SHOU.Launcher.Core.dll'}else{Join-Path $out 'SHOU Launcher/SHOU.Launcher.Core.dll'}
            $presentA=Owner-Contains $owner $a;$presentB=Owner-Contains $owner $b
            Require ($presentA -eq ($case -eq 'A') -and $presentB -eq ($case -eq 'B')) 'B07 stale Microsoft input survived normal clean/publish.'
            Require ((Owner-Contains $owner $cfA) -eq ($case -eq 'A')) 'B07 stale first escaped key survived.'
            Require ((Owner-Contains $owner $cfB) -eq ($case -eq 'B')) 'B07 stale second escaped key survived.'
            Require (-not (Owner-Contains $owner $unlisted)) 'B06 publish leaked non-allowlisted fake token.'
            $info=Get-Content -Raw (Join-Path $out 'BUILD-INFO.json') | ConvertFrom-Json
            Require ($info.source_sha -eq $sha -and $info.runtime -eq $SequenceRid -and $info.configuration -eq 'Release') 'B07 identity/RID/configuration changed.'
            $null=New-Item -ItemType Directory -Path (Join-Path $report $case) -Force
            Copy-Item -LiteralPath (Join-Path $intermediate 'reports/steps.json') -Destination (Join-Path $report "$case/steps.json")
            [IO.File]::WriteAllText((Join-Path $out 'FIXTURE-ONLY.txt'),'Contains fake configuration inputs for B07; not a product candidate.')
            $cases.Add(@{id=$current;status='PASS';build_root=$intermediate;entry='src/SHOU.Launcher.Desktop/SHOU.Launcher.Desktop.csproj';configuration='Release';rid=$SequenceRid;source_sha=$sha;owner_sha256=(Get-FileHash $owner).Hash;fixture_only=$true;output=$out})
        }
    }
} catch {$failure=$_.Exception;throw}
finally {
    Clear-Inputs
    foreach($name in $saved.Keys){
        if($null -eq $saved[$name]){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}
        else{[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
    }
    $restored=$true
    foreach($name in $saved.Keys){if([Environment]::GetEnvironmentVariable($name,'Process') -cne $saved[$name]){$restored=$false}}
    $result=@{status=$(if($failure -or -not $restored){'FAIL'}else{'PASS'});mode=$Mode;current_case=$current;cases=$cases.ToArray();environment_restored=$restored;error_type=$(if($failure){$failure.GetType().FullName}else{$null});at=[DateTimeOffset]::UtcNow.ToString('o')}
    $temporary=Join-Path $report 'build-contract.tmp';$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $report 'build-contract.json') -Force
    if(-not $restored){throw 'Build-contract caller environment was not restored.'}
}
