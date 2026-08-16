param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{32}$')]
    [string]$Checksum,

    [Parameter(Mandatory = $true)]
    [string]$Timestamp
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root 'manifest.json'
$manifest = Get-Content -Raw $path | ConvertFrom-Json
$plugin = $manifest[0]
$sourceUrl = "https://github.com/jensdufour/PUB-Jellyfin-Xtream-PostProcessor/releases/download/v$Version/xtream-post-processor_$Version.zip"
$entry = [pscustomobject]@{
    version = $Version
    changelog = "Xtream Post Processor $Version"
    targetAbi = '10.11.0.0'
    sourceUrl = $sourceUrl
    checksum = $Checksum
    timestamp = $Timestamp
}
$existing = @($plugin.versions | Where-Object { $_.version -ne $Version })
$plugin.versions = @($entry) + $existing
ConvertTo-Json -InputObject @($plugin) -Depth 10 | Set-Content $path -Encoding utf8
