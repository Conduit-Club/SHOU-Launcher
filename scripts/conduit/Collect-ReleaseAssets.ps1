[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Codename,
    [switch]$Prerelease
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$inputPath=(Resolve-Path -LiteralPath $InputRoot).Path
$outputPath=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $outputPath){throw 'Release output must be new; existing packages are not overwritten.'}
$rids=@('osx-arm64','win-x64','win-arm64')
$manifest=Get-Content -Raw -LiteralPath (Join-Path $repo 'src/SHOU.Launcher.Desktop/Native/MacOS/frozen-arm64-v1/manifest.json') | ConvertFrom-Json
$packages=@{}
# upload-artifact contains both dist and regression reports. Only the known
# distribution roots may supply assets; report ZIP fixtures can share tag names.
$distRoots=@()
if(Test-Path -LiteralPath (Join-Path $inputPath 'dist') -PathType Container){$distRoots+=Join-Path $inputPath 'dist'}
foreach($artifact in Get-ChildItem -LiteralPath $inputPath -Directory){
    $dist=Join-Path $artifact.FullName 'dist'
    if($artifact.Name -like 'Desktop-Release-*' -and (Test-Path -LiteralPath $dist -PathType Container)){$distRoots+=$dist}
}
function StreamHash([IO.Stream]$stream){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()}
function EntryHash($entry){$stream=$entry.Open();try{StreamHash $stream}finally{$stream.Dispose()}}
function Assert-WindowsMachine($entry,[int]$machine){
    $stream=$entry.Open()
    try{
        $dos=[byte[]]::new(64);$stream.ReadExactly($dos)
        if($dos[0] -ne 0x4d -or $dos[1] -ne 0x5a){throw 'Windows payload has no DOS header.'}
        $offset=[BitConverter]::ToUInt32($dos,60)
        if($offset -lt 64 -or $offset -gt $entry.Length-6){throw 'Windows PE offset is invalid.'}
        $remaining=[long]$offset-64;$buffer=[byte[]]::new(8192)
        while($remaining -gt 0){$n=$stream.Read($buffer,0,[int][Math]::Min($buffer.Length,$remaining));if($n -eq 0){throw 'Truncated Windows payload.'};$remaining-=$n}
        $pe=[byte[]]::new(6);$stream.ReadExactly($pe)
        if([BitConverter]::ToUInt32($pe,0) -ne 0x4550 -or [BitConverter]::ToUInt16($pe,4) -ne $machine){throw 'Windows payload architecture does not match its package.'}
    }finally{$stream.Dispose()}
}
foreach($file in @($distRoots | ForEach-Object {Get-ChildItem -LiteralPath $_ -Filter 'BUILD-INFO.json' -File -Recurse})){
    $info=Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
    if($info.entry -ne 'Avalonia Desktop' -or $info.runtime -notin $rids){continue}
    $name="SHOU-Launcher-$($info.runtime)-$Tag.zip"
    $archive=Join-Path $file.DirectoryName $name
    # Nested app metadata and test reports are not distributable package roots.
    if(-not (Test-Path -LiteralPath $archive -PathType Leaf)){continue}
    if($packages.ContainsKey($info.runtime)){throw "Duplicate package for $($info.runtime)."}
    if($info.source_sha -cne $SourceSha -or $info.source_dirty -ne $false -or $info.version -cne $Version -or
       $info.codename -cne $Codename -or $info.label -cne $Tag -or $info.release_tag -cne $Tag -or
       $info.prerelease -ne [bool]$Prerelease -or $info.configuration -ne 'Release' -or $info.self_contained -ne $true){
        throw "Package identity/source/release mode mismatch: $($info.runtime)."
    }
    if($info.credential_verification.MS_CLIENT_IDConfigured -ne $true -or $info.credential_verification.CURSEFORGE_API_KEYEmbedded -ne $true){throw 'Release client configuration was not verified.'}
    $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecar=Get-Content -Raw -LiteralPath ($archive+'.sha256')
    if($sidecar.Trim() -cne "$hash  $name"){throw 'Package checksum sidecar does not match its ZIP.'}
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try{
        if($info.runtime.StartsWith('win-')){
            $entries=@($zip.Entries | Where-Object {-not $_.FullName.EndsWith('/')})
            if($entries.Count -ne 1 -or $entries[0].FullName -cne 'SHOU.Launcher.Desktop.exe'){throw 'Windows release must contain one EXE and no loose DLLs.'}
            $entry=$entries[0]
            Assert-WindowsMachine $entry $(if($info.runtime -eq 'win-x64'){0x8664}else{0xaa64})
            if($info.payload.name -cne $entry.FullName -or $info.payload.bytes -ne $entry.Length -or $info.payload.sha256 -cne (EntryHash $entry)){throw 'Windows payload differs from the verified single-file build.'}
        }else{
            $broker=$zip.GetEntry('SHOU Launcher.app/Contents/Helpers/SHOU.CredentialBroker')
            $embedded=$zip.GetEntry('SHOU Launcher.app/Contents/MacOS/BUILD-INFO.json')
            if(-not $zip.GetEntry('SHOU Launcher.app/Contents/Info.plist') -or -not $zip.GetEntry('SHOU Launcher.app/Contents/MacOS/SHOU.Launcher.Core.dll') -or
               -not $broker -or -not $embedded){throw 'Mac release is missing its app, managed payload or credential component.'}
            if((EntryHash $broker) -cne $manifest.payload_sha256 -or $info.signing_identity -ne $manifest.signing_identity -or
               $info.credential_broker.frozen_payload_sha256 -cne $manifest.payload_sha256){throw 'Mac frozen credential/signing identity changed.'}
            if((EntryHash $embedded) -cne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'Mac embedded and external build metadata differ.'}
            foreach($document in @('SIGNING-INFO.json','CREDENTIAL-BROKER-INFO.json')){
                $signed=Get-Content -Raw -LiteralPath (Join-Path $file.DirectoryName $document) | ConvertFrom-Json
                if($signed.identity -ne $manifest.signing_identity){throw 'Mac signing evidence belongs to another identity.'}
            }
        }
    }finally{$zip.Dispose()}
    $packages[$info.runtime]=@{archive=$archive;metadata=$file.FullName;sha256=$hash;bytes=(Get-Item -LiteralPath $archive).Length}
}
if($packages.Count -ne $rids.Count){throw 'All three Desktop packages are required; a partial release is not created.'}
$stage=$outputPath+'.stage-'+[guid]::NewGuid().ToString('N')
$null=New-Item -ItemType Directory -Path $stage
try{
    $checksums=@();$records=@()
    foreach($rid in $rids){
        $item=$packages[$rid];$name=[IO.Path]::GetFileName($item.archive)
        Copy-Item -LiteralPath $item.archive,($item.archive+'.sha256') -Destination $stage
        Copy-Item -LiteralPath $item.metadata -Destination (Join-Path $stage "BUILD-INFO-$rid.json")
        if($rid -eq 'osx-arm64'){
            foreach($document in @('SIGNING-INFO','CREDENTIAL-BROKER-INFO')){
                Copy-Item -LiteralPath (Join-Path (Split-Path $item.metadata) ($document+'.json')) -Destination (Join-Path $stage "$document-$rid.json")
            }
        }
        $checksums+="$($item.sha256)  $name"
        $records+=@{runtime=$rid;file=$name;sha256=$item.sha256;bytes=$item.bytes}
    }
    $checksums | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding ascii
    $summary=@{version=$Version;codename=$Codename;tag=$Tag;source_sha=$SourceSha;prerelease=[bool]$Prerelease;entry='Avalonia Desktop';packages=$records}
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'RELEASE-MANIFEST.json') -Encoding utf8
    [IO.Directory]::Move($stage,$outputPath)
    $summary | ConvertTo-Json -Depth 5
}finally{if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}}
