function Read-ConduitBuildCredentials {
    [CmdletBinding()]
    param([string]$EnvPath)

    # Parse data only: in particular, the '$' characters in a CurseForge key are literal.
    $supported = @('PCL_MS_CLIENT_ID', 'PCL_CURSEFORGE_API_KEY', 'CURSEFORGE_API_KEY', 'CURSEFORGE_API')
    $values = @{}
    if (Test-Path -LiteralPath $EnvPath) {
        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $EnvPath) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
            if ($line -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -or $Matches[1] -notin $supported) {
                throw "Unsupported .env assignment at line $lineNumber. See .env.example."
            }
            $name, $value = $Matches[1], $Matches[2]
            if ($value -match '^(["''])(.*)\1$') {
                $value = $Matches[2]
            } elseif ($value -match '\s|["'']') {
                throw "Invalid .env value at line $lineNumber. Use a single literal value, optionally quoted."
            }
            $values[$name] = $value
        }
    }
    function Resolve-BuildValue([string[]]$Names) {
        foreach ($name in $Names) {
            $value = [Environment]::GetEnvironmentVariable($name, 'Process')
            if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
        }
        foreach ($name in $Names) {
            if (-not [string]::IsNullOrWhiteSpace($values[$name])) { return $values[$name] }
        }
        return ''
    }
    [pscustomobject]@{
        MicrosoftClientId = Resolve-BuildValue @('PCL_MS_CLIENT_ID')
        CurseForgeApiKey = Resolve-BuildValue @('PCL_CURSEFORGE_API_KEY', 'CURSEFORGE_API_KEY', 'CURSEFORGE_API')
    }
}
