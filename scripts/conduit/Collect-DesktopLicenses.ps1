[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference='Stop'
$deps=Get-Content -Raw -LiteralPath (Join-Path $PublishDirectory 'SHOU.Launcher.Desktop.deps.json') | ConvertFrom-Json -AsHashtable
$destination=Join-Path $PublishDirectory 'licenses'
$null=New-Item -ItemType Directory -Path $destination -Force
$lucide=Join-Path $PSScriptRoot '../../src/SHOU.Launcher.Desktop/Assets/Icons/Lucide'
$lucideDestination=Join-Path $destination 'lucide-static-1.17.0'
$null=New-Item -ItemType Directory -Path $lucideDestination -Force
Copy-Item -LiteralPath (Join-Path $lucide 'LICENSE.txt'),(Join-Path $lucide 'SOURCE.txt') -Destination $lucideDestination
$packages=@()
foreach ($library in $deps.libraries.GetEnumerator() | Where-Object { $_.Value.type -in @('package','runtimepack') }) {
    $parts=$library.Key.Split('/',2);$name=$parts[0] -replace '^runtimepack\.','';$version=$parts[1]
    if ($name -notmatch '^[A-Za-z0-9_.-]+$' -or $version -notmatch '^[A-Za-z0-9_.+-]+$') { throw 'Invalid dependency identity.' }
    $package=Join-Path $env:NUGET_PACKAGES "$($name.ToLowerInvariant())/$version"
    $nuspec=Get-ChildItem -LiteralPath $package -Filter '*.nuspec' -File | Select-Object -First 1
    if (-not $nuspec) { throw "Dependency license metadata missing: $name/$version" }
    [xml]$xml=Get-Content -Raw -LiteralPath $nuspec.FullName
    $metadata=$xml.package.metadata
    $folder=Join-Path $destination "$name-$version"
    $null=New-Item -ItemType Directory -Path $folder -Force
    $copied=@()
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { $_.Name -match '^(?i:licen[sc]e|notice|third[-_]party)' }) {
        $relative=[IO.Path]::GetRelativePath($package,$file.FullName)
        $target=Join-Path $folder $relative
        $null=New-Item -ItemType Directory -Path (Split-Path $target) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $copied+=$relative
    }
    $expression=[string]$metadata.license.InnerText
    if ($expression -eq 'MIT' -and -not ($copied | Where-Object { $_ -match '(?i)licen[sc]e' })) {
        $copyright=[string]$metadata.copyright
        @"
MIT License
$copyright
Package: $name $version

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@ | Set-Content -LiteralPath (Join-Path $folder 'LICENSE-MIT.txt') -Encoding utf8
        $copied+='LICENSE-MIT.txt'
    }
    $packages+=@{name=$name;version=$version;license=$expression;copyright=[string]$metadata.copyright;
        project_url=[string]$metadata.projectUrl;license_url=[string]$metadata.licenseUrl;files=$copied}
}
$packages | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'PACKAGES.json') -Encoding utf8
Write-Host "Collected license metadata/text for $($packages.Count) runtime packages."
