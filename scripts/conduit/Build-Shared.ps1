# Shared input and environment contract for legacy and Avalonia hosts.
. (Join-Path $PSScriptRoot 'BuildEnvironment.ps1')

function Resolve-ConduitCanonicalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root
    foreach ($part in $full.Substring($root.Length).Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.LinkTarget) { $current = $item.ResolveLinkTarget($true).FullName }
        }
    }
    return [IO.Path]::GetFullPath($current)
}
function Assert-ConduitTaskPath([string]$Path, [string]$TaskRoot = $env:SHOU_DEV_ROOT) {
    if ([string]::IsNullOrWhiteSpace($TaskRoot)) { throw 'SHOU_DEV_ROOT is required; use scripts/dev/Invoke-Dev.ps1.' }
    $root = (Resolve-ConduitCanonicalPath $TaskRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolved = Resolve-ConduitCanonicalPath $Path
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw "Task path escaped registered root: $resolved" }
    return $resolved
}
function Assert-ConduitHeavyGate {
    if (-not $env:SHOU_WORKER -or -not (Test-Path -LiteralPath (Join-Path $env:SHOU_DEV_ROOT 'state/heavy-owner.json'))) {
        throw 'Heavy build must run through control/dev.py (or scripts/dev/Invoke-Dev.ps1).'
    }
    $owner = Get-Content -Raw -LiteralPath (Join-Path $env:SHOU_DEV_ROOT 'state/heavy-owner.json') | ConvertFrom-Json
    if ($owner.worker -ne $env:SHOU_WORKER) { throw 'Heavy task owner mismatch.' }
}
function New-ConduitJavaBootstrap([string]$Repo,[string]$BuildRoot) {
    Assert-ConduitHeavyGate
    $build=Assert-ConduitTaskPath $BuildRoot
    $source=@(Get-ChildItem -LiteralPath (Join-Path $Repo 'src/SHOU.Launcher.Core/Java/Bootstrap') -Filter '*.java' -File | Sort-Object Name)
    if(-not $source.Count){throw 'Trusted Java bootstrap source is missing.'}
    $registry=Get-Content -Raw -LiteralPath (Join-Path $env:SHOU_DEV_ROOT 'control/tools.json') | ConvertFrom-Json
    $suffix=if($IsWindows){'.exe'}else{''}
    if($registry.java.home) {
        $javac=Join-Path $registry.java.home ('bin/javac'+$suffix)
        if(-not (Test-Path -LiteralPath $javac -PathType Leaf)){throw 'Registered Java compiler is unavailable; no system JDK change will be attempted.'}
    } elseif($registry.java.path) {
        $compiler=Get-ChildItem -LiteralPath $registry.java.path -Filter ('javac'+$suffix) -File -Recurse | Select-Object -First 1
        if(-not $compiler){throw 'Registered Java compiler is unavailable; no system JDK change will be attempted.'}
        $javac=$compiler.FullName
    } else {throw 'A registered Java compiler is required for the trusted bootstrap.'}
    $jar=Join-Path (Split-Path $javac) ('jar'+$suffix)
    if(-not (Test-Path -LiteralPath $jar -PathType Leaf)){throw 'Registered jar tool is missing.'}
    $saved=@{}
    foreach($name in (@((Get-ChildItem Env:PCL_*).Name)+@('CURSEFORGE_API_KEY','CURSEFORGE_API') | Select-Object -Unique)){
        $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
        Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue
    }
    try {
        $version=(& $javac '-J-XX:-UsePerfData' "-J-Djava.io.tmpdir=$env:TMPDIR" --version) -join "`n"
        if($LASTEXITCODE -ne 0){throw 'Cannot read Java compiler version.'}
        $fingerprint=(@($source | ForEach-Object {$_.Name+':'+(Get-FileHash $_.FullName -Algorithm SHA256).Hash})+@($version))-join "`n"
        $sourceHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fingerprint)))
        $root=Assert-ConduitTaskPath (Join-Path $build ('java-bootstrap/'+$sourceHash))
        $classes=Join-Path $root 'classes';$null=New-Item -ItemType Directory -Path $classes -Force
        $target=Join-Path $root 'bootstrap.jar'
        $record=Join-Path $root 'build.json'
        if(Test-Path -LiteralPath $record) {
            $previous=Get-Content -Raw -LiteralPath $record | ConvertFrom-Json
            if((Test-Path -LiteralPath $target) -and (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $previous.sha256){return $previous}
            throw 'Cached trusted Java bootstrap changed; use another registered build job.'
        }
        $arguments=@('-J-XX:-UsePerfData',"-J-Djava.io.tmpdir=$env:TMPDIR",'--release','8','-encoding','UTF-8','-g:none','-proc:none','-d',$classes)+@($source.FullName)
        & $javac @arguments
        if($LASTEXITCODE -ne 0){throw 'Trusted Java bootstrap compilation failed.'}
        & $jar '-J-XX:-UsePerfData' "-J-Djava.io.tmpdir=$env:TMPDIR" --create --file $target --date=2000-01-01T00:00:00Z -C $classes org/shou/launcher/bootstrap
        if($LASTEXITCODE -ne 0){throw 'Trusted Java bootstrap archive failed.'}
        $info=[pscustomobject]@{path=$target;sha256=(Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant();source_sha256=$sourceHash;compiler=$version;target_class_version=52;entry='org.shou.launcher.bootstrap.BootstrapMain'}
        $info | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $record -Encoding utf8
        return $info
    } finally {
        foreach($name in $saved.Keys){
            if($null -eq $saved[$name]){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}
            else{[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
        }
    }
}
function Get-ConduitBuildInput([string]$Repo, [string]$EnvFile = '.env') {
    $path = if ([IO.Path]::IsPathRooted($EnvFile)) { $EnvFile } else { Join-Path $Repo $EnvFile }
    if ($EnvFile -ne '.env' -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Explicit EnvFile is not readable.' }
    # Parse the whole file first, even if process variables would win.
    $credentials = Read-ConduitBuildCredentials -EnvPath $path
    if ($credentials.MicrosoftClientId -and -not [guid]::TryParse($credentials.MicrosoftClientId, [ref]([guid]::Empty))) { throw 'PCL_MS_CLIENT_ID must be an application GUID.' }
    $source = @{}
    foreach ($entry in @(@('Microsoft', @('PCL_MS_CLIENT_ID')), @('CurseForge', @('PCL_CURSEFORGE_API_KEY','CURSEFORGE_API_KEY','CURSEFORGE_API')))) {
        $fromProcess = $false
        foreach ($name in $entry[1]) { if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Process'))) { $fromProcess = $true; break } }
        $source[$entry[0]] = if ($fromProcess) { 'process' } elseif (Test-Path -LiteralPath $path) { 'env_file_or_empty' } else { 'empty_missing_default_file' }
    }
    return [pscustomobject]@{ Credentials = $credentials; EnvPath = $path; Sources = $source }
}
function Invoke-ConduitBuildEnvironment([string]$Repo, [string]$EnvFile, [scriptblock]$Action) {
    $inputConfig = Get-ConduitBuildInput -Repo $Repo -EnvFile $EnvFile
    $saved = @{}
    $names = @((Get-ChildItem Env:PCL_*).Name) + @('CURSEFORGE_API_KEY','CURSEFORGE_API')
    foreach ($name in ($names | Select-Object -Unique)) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    try {
        foreach ($name in $saved.Keys) { Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue }
        $env:PCL_WRITE_SECRET = '1'
        $env:PCL_MS_CLIENT_ID = $inputConfig.Credentials.MicrosoftClientId
        $env:PCL_CURSEFORGE_API_KEY = $inputConfig.Credentials.CurseForgeApiKey
        $env:PCL_GITHUB_SHA = & git -C $Repo rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve actual checkout SHA.' }
        & $Action $inputConfig
    } finally {
        Get-ChildItem Env:PCL_* | ForEach-Object { Remove-Item -LiteralPath ('Env:'+$_.Name) -ErrorAction SilentlyContinue }
        foreach ($name in $saved.Keys) {
            if($null -eq $saved[$name]) { Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
        }
    }
}
function Invoke-ConduitDotnet([string]$Dotnet, [string[]]$Arguments, [string]$LogPath) {
    & $Dotnet @Arguments 2>&1 | Tee-Object -FilePath $LogPath | Out-Host
    $code = $LASTEXITCODE
    $stepsPath = Join-Path (Split-Path $LogPath) 'steps.json'
    $steps = @()
    if (Test-Path -LiteralPath $stepsPath) { $steps = @(Get-Content -Raw -LiteralPath $stepsPath | ConvertFrom-Json) }
    $steps += [pscustomobject]@{ command = @($Dotnet) + $Arguments; cwd = (Get-Location).Path; exit_code = $code; log = $LogPath; time = [DateTimeOffset]::UtcNow.ToString('o') }
    $steps | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $stepsPath -Encoding utf8
    if ($code -ne 0) { throw "dotnet step failed (exit $code); log: $LogPath" }
}
function Assert-ConduitProjectPaths([string]$Dotnet, [string[]]$Projects, [string[]]$Properties, [string]$ReportPath) {
    $names = 'OutputPath,BaseIntermediateOutputPath,IntermediateOutputPath,MSBuildProjectExtensionsPath,ProjectAssetsFile,PublishDir,PackageOutputPath,TargetPath'
    $reports = @()
    $artifactProperty=@($Properties | Where-Object { $_.StartsWith('-p:ArtifactsPath=',[StringComparison]::OrdinalIgnoreCase) })
    $expected=if($artifactProperty.Count){(Resolve-ConduitCanonicalPath $artifactProperty[-1].Substring(17)).TrimEnd([IO.Path]::DirectorySeparatorChar)}else{$null}
    $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    foreach ($project in $Projects) {
        $raw = & $Dotnet msbuild $project "-getProperty:$names" @Properties
        if ($LASTEXITCODE -ne 0) { throw 'Cannot evaluate output paths.' }
        $values = ($raw -join "`n") | ConvertFrom-Json
        foreach ($entry in $values.Properties.PSObject.Properties) {
            if ($entry.Value) {
                $path = if ([IO.Path]::IsPathRooted($entry.Value)) { $entry.Value } else { Join-Path (Split-Path $project) $entry.Value }
                $canonical = Assert-ConduitTaskPath $path
                if($expected -and -not ($canonical.Equals($expected,$comparison) -or $canonical.StartsWith($expected+[IO.Path]::DirectorySeparatorChar,$comparison))) {
                    throw "Evaluated project output escaped this job root: $($entry.Name)"
                }
            }
        }
        $reports += [pscustomobject]@{ project = $project; properties = $values.Properties }
    }
    $reports | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
