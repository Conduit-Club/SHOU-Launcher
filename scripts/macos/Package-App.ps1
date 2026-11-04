[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$ArchiveStem)
$ErrorActionPreference='Stop'
if (-not $IsMacOS) { throw 'Local Mac packaging/signature inspection requires macOS.' }
. (Join-Path $PSScriptRoot '../conduit/Build-Shared.ps1')
Assert-ConduitHeavyGate
$publish=Assert-ConduitTaskPath $PublishDirectory
$output=Assert-ConduitTaskPath $OutputDirectory
$app=Join-Path $output 'SHOU Launcher.app'
$mac=Join-Path $app 'Contents/MacOS'
$res=Join-Path $app 'Contents/Resources'
$null=New-Item -ItemType Directory -Path (Split-Path $mac),$res -Force
Copy-Item -LiteralPath $publish -Destination $mac -Recurse
# Resource directories do not belong in the code directory: codesign treats dotted
# package/version directories there as nested bundles instead of ordinary resources.
foreach ($name in @('licenses','LICENSE','LICENCE','THIRD-PARTY-METADATA.json')) {
    $source=Join-Path $mac $name
    if (Test-Path -LiteralPath $source) { Move-Item -LiteralPath $source -Destination (Join-Path $res $name) }
}
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Copy-Item -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/Images/Conduit/club-logo.png') -Destination $res
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'Plain Craft Launcher 2/LICENCE') -Destination $res
$iconset=Join-Path $env:TMPDIR ('shou-'+[guid]::NewGuid().ToString('N')+'.iconset')
$null=New-Item -ItemType Directory -Path $iconset
foreach ($size in @(16,32,128,256,512)) {
    foreach ($scale in @(1,2)) {
        $name="icon_$($size)x$($size)"+$(if ($scale -eq 2) {'@2x'} else {''})+'.png'
        & /usr/bin/sips -z ($size*$scale) ($size*$scale) (Join-Path $res 'club-logo.png') --out (Join-Path $iconset $name) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot generate bundle icon.' }
    }
}
& /usr/bin/iconutil -c icns $iconset -o (Join-Path $res 'SHOU.icns')
if ($LASTEXITCODE -ne 0) { throw 'Cannot encode bundle icon.' }
@"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>SHOU.Launcher.Desktop</string>
<key>CFBundleIdentifier</key><string>club.conduit.shou.launcher</string>
<key>CFBundleName</key><string>SHOU Launcher</string>
<key>CFBundleDisplayName</key><string>潮涌核心社启动器</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>SHOU.icns</string>
<key>CFBundleVersion</key><string>$Version</string>
<key>CFBundleShortVersionString</key><string>$Version</string>
<key>NSHighResolutionCapable</key><true/>
<key>LSMinimumSystemVersion</key><string>14.0</string>
</dict></plist>
"@ | Set-Content -LiteralPath (Join-Path $app 'Contents/Info.plist') -Encoding utf8
& /bin/chmod +x (Join-Path $mac 'SHOU.Launcher.Desktop')
if ($LASTEXITCODE -ne 0) { throw 'Cannot set apphost executable permission.' }
$nativeReport=@()
foreach ($file in Get-ChildItem -LiteralPath $mac -File -Recurse) {
    $kind = & /usr/bin/file -b $file.FullName
    if ($kind -match 'Mach-O') {
        $arch = & /usr/bin/lipo -archs $file.FullName
        if ($LASTEXITCODE -ne 0 -or $arch -notmatch 'arm64') { throw "Native dependency is not arm64: $($file.Name)" }
        $links = & /usr/bin/otool -L $file.FullName
        $dependencyRows = @($links | Where-Object { $_ -match '^\s+' })
        if ($LASTEXITCODE -ne 0 -or ($dependencyRows -join "`n") -match '/opt/homebrew|/Users/[^/]+/') { throw "Unexpected developer-machine native dependency: $($file.Name)" }
        $nativeReport += @{file=$file.Name;architectures=$arch;dependencies=$dependencyRows;signature='ad-hoc'}
    }
    # All files in Contents/MacOS are code subcomponents, including PE DLLs and JSON data.
    # Follow Avalonia's explicit per-file signing order, then sign the apphost/bundle last.
    # codesign stores generic-file signatures in xattrs; ditto preserves them in the archive.
    if ($file.Name -eq 'SHOU.Launcher.Desktop') { continue }
    & /usr/bin/codesign --force --sign - $file.FullName 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Local native signing failed: $($file.Name)" }
    & /usr/bin/codesign --verify --strict $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Individual signature verification failed: $($file.Name)" }
}
& /usr/bin/codesign --force --sign - --identifier club.conduit.shou.launcher --entitlements (Join-Path $PSScriptRoot 'Entitlements.plist') $app 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Local bundle signing failed.' }
& /usr/bin/codesign --verify --strict $app
if ($LASTEXITCODE -ne 0) { throw 'Local bundle signature verification failed.' }
$nativeReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'native-dependencies.json')
& /usr/bin/ditto -c -k --sequesterRsrc --keepParent $app (Join-Path $output ($ArchiveStem+'.zip'))
if ($LASTEXITCODE -ne 0) { throw 'Bundle archive failed.' }
