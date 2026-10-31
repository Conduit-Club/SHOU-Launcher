[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'

if (-not ('Conduit.ClientCredentialVerifier' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
namespace Conduit {
    public static class ClientCredentialVerifier {
        public static bool Contains(byte[] assembly, string value) {
            return ((ReadOnlySpan<byte>)assembly).IndexOf(Encoding.Unicode.GetBytes(value)) >= 0;
        }
    }
}
'@
}
$assembly = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $AssemblyPath).Path)
$checks = [ordered]@{}
foreach ($name in @('MS_CLIENT_ID', 'CURSEFORGE_API_KEY', 'GITHUB_SHA')) {
    $value = [Environment]::GetEnvironmentVariable('PCL_' + $name, 'Process')
    $configured = -not [string]::IsNullOrWhiteSpace($value)
    $embedded = $configured -and [Conduit.ClientCredentialVerifier]::Contains($assembly, $value)
    if ($configured -and -not $embedded) {
        throw "Configured $name was not found in the compiled client assembly. Rebuild with fresh compiler state."
    }
    $checks[$name + 'Configured'] = $configured
    $checks[$name + 'Embedded'] = $embedded
}
Write-Host "Client credential verification: Microsoft=$($checks.MS_CLIENT_IDEmbedded); CurseForge=$($checks.CURSEFORGE_API_KEYEmbedded); commit=$($checks.GITHUB_SHAEmbedded). Values are not printed."
[pscustomobject]$checks
