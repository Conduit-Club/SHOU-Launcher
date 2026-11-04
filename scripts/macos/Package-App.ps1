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
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Copy-Item -LiteralPath (Join-Path $repo 'Plain Craft Launcher 2/Images/Conduit/club-logo.png') -Destination $res
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'Plain Craft Launcher 2/LICENCE') -Destination $res
@"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>SHOU.Launcher.Desktop</string>
<key>CFBundleIdentifier</key><string>club.conduit.shou.launcher</string>
<key>CFBundleName</key><string>SHOU Launcher</string>
<key>CFBundleDisplayName</key><string>潮涌核心社启动器</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>$Version</string>
<key>CFBundleShortVersionString</key><string>$Version</string>
<key>NSHighResolutionCapable</key><true/>
<key>LSMinimumSystemVersion</key><string>14.0</string>
</dict></plist>
"@ | Set-Content -LiteralPath (Join-Path $app 'Contents/Info.plist') -Encoding utf8
& /bin/chmod +x (Join-Path $mac 'SHOU.Launcher.Desktop')
if ($LASTEXITCODE -ne 0) { throw 'Cannot set apphost executable permission.' }
$nativeReport=@()
foreach ($file in Get-ChildItem -LiteralPath $mac -File) {
    $kind = & /usr/bin/file -b $file.FullName
    if ($kind -notmatch 'Mach-O') { continue }
    $arch = & /usr/bin/lipo -archs $file.FullName
    if ($LASTEXITCODE -ne 0 -or $arch -notmatch 'arm64') { throw "Native dependency is not arm64: $($file.Name)" }
    $links = & /usr/bin/otool -L $file.FullName
    if ($LASTEXITCODE -ne 0 -or ($links -join "`n") -match '/opt/homebrew|/Users/[^/]+/') { throw "Unexpected developer-machine native dependency: $($file.Name)" }
    # Sign each actual Mach-O independently. ad-hoc is only local execution, never notarization.
    & /usr/bin/codesign --force --sign - $file.FullName 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Local native signing failed: $($file.Name)" }
    $nativeReport += @{file=$file.Name;architectures=$arch;dependencies=$links;signature='ad-hoc'}
}
& /usr/bin/codesign --force --sign - $app 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Local bundle signing failed.' }
& /usr/bin/codesign --verify --strict $app
if ($LASTEXITCODE -ne 0) { throw 'Local bundle signature verification failed.' }
$nativeReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'native-dependencies.json')
& /usr/bin/ditto -c -k --sequesterRsrc --keepParent $app (Join-Path $output ($ArchiveStem+'.zip'))
if ($LASTEXITCODE -ne 0) { throw 'Bundle archive failed.' }
