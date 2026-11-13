[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$CoreAssembly, [Parameter(Mandatory)][string]$ReportPath,
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier)
$ErrorActionPreference='Stop'
$sdk=& $Dotnet --version
$model=Join-Path (Split-Path $Dotnet) "sdk/$sdk/Microsoft.NET.HostModel.dll"
if (-not (Test-Path -LiteralPath $model)) { throw 'The locked SDK bundle verifier is missing.' }
$null=[Reflection.Assembly]::LoadFrom($model)
[long]$header=0
if (-not [Microsoft.NET.HostModel.Bundle.Bundler]::IsBundle($Executable,[ref]$header)) { throw 'Windows output is not a .NET bundle.' }
$stream=[IO.File]::OpenRead($Executable)
$reader=[IO.BinaryReader]::new($stream)
try {
    $stream.Position=$header
    $major=$reader.ReadUInt32(); $minor=$reader.ReadUInt32(); $count=$reader.ReadInt32(); $null=$reader.ReadString()
    if ($major -ne 6 -or $minor -ne 0 -or $count -lt 10 -or $count -gt 10000) { throw 'Unexpected SDK bundle format.' }
    # SDK Manifest.Write: two config locations and flags, followed by FileEntry.Write records.
    # https://github.com/dotnet/runtime/blob/main/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs
    for($i=0;$i -lt 5;$i++){ $null=$reader.ReadInt64() }
    $entries=@()
    for($i=0;$i -lt $count;$i++) {
        $offset=$reader.ReadInt64();$size=$reader.ReadInt64();$compressed=$reader.ReadInt64();$type=$reader.ReadByte();$name=$reader.ReadString()
        if($offset -lt 0 -or $size -le 0 -or $compressed -ne 0 -or $offset+$size -gt $header -or $name.StartsWith('/') -or $name.Split('/') -contains '..') { throw 'Invalid or compressed bundle entry.' }
        $entries+= [pscustomobject]@{name=$name;offset=$offset;size=$size;type=$type}
    }
    foreach($name in @('SHOU.Launcher.Desktop.dll','SHOU.Launcher.Core.dll','System.Private.CoreLib.dll','libSkiaSharp.dll','libHarfBuzzSharp.dll','SHOU.Launcher.Desktop.runtimeconfig.json','SHOU.Launcher.Desktop.deps.json')) {
        if(@($entries | Where-Object name -EQ $name).Count -ne 1){throw "Required bundle entry is missing/duplicated: $name"}
    }
    if($entries | Where-Object name -Match '(?i)(^|/)(\.env|UserKey\.bin|shou\.desktop\.v1\.json|config\.v1\.(json|yml)|credentials-v1)(/|$)'){throw 'Private data entered the bundle.'}
    # The SDK's singlefilehost statically contains the Windows runtime; coreclr/clrjit
    # need not appear as separate manifest entries. Require its self-contained config.
    $runtimeEntry=$entries | Where-Object name -EQ 'SHOU.Launcher.Desktop.runtimeconfig.json'
    $stream.Position=$runtimeEntry.offset
    $runtime=[Text.Encoding]::UTF8.GetString($reader.ReadBytes([int]$runtimeEntry.size)) | ConvertFrom-Json
    if($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or -not ($runtime.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App')){throw 'Bundle is not configured as self-contained.'}
    $core=$entries | Where-Object name -EQ 'SHOU.Launcher.Core.dll'
    $stream.Position=$core.offset; $coreBytes=$reader.ReadBytes([int]$core.size)
    $coreHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($coreBytes))
    if($coreHash -ne (Get-FileHash -LiteralPath $CoreAssembly -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'Bundled Core differs from the fresh build owner.'}
    $machine=if($RuntimeIdentifier -eq 'win-x64'){0x8664}else{0xAA64}
    $native=@()
    foreach($entry in @([pscustomobject]@{name='apphost';offset=0;type=2}) + @($entries | Where-Object type -EQ 2)) {
        $stream.Position=$entry.offset
        if($reader.ReadUInt16() -ne 0x5A4D){throw 'Expected a Windows PE native component.'}
        $stream.Position=$entry.offset+0x3C;$pe=$reader.ReadInt32();$stream.Position=$entry.offset+$pe
        if($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $machine){throw 'Native component architecture mismatch.'}
        $native+=$entry.name
    }
    [pscustomobject]@{format="$major.$minor";entryCount=$count;coreSha256=$coreHash;selfContained=$true;includedFrameworks=$runtime.runtimeOptions.includedFrameworks;nativeArchitecture=$RuntimeIdentifier;nativeComponents=$native;entries=$entries} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Host "Verified single-file bundle: $count entries; native components match $RuntimeIdentifier; Core matches fresh build."
} finally {$reader.Dispose()}
