param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Jellyfin.Plugin.XtreamPostProcessor/Jellyfin.Plugin.XtreamPostProcessor.csproj'
$output = Join-Path $root "dist/$Version"
$publish = Join-Path $output 'publish'
$archive = Join-Path $output "xtream-post-processor_$Version.zip"

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item $publish -ItemType Directory -Force | Out-Null

dotnet publish $project --configuration Release --output $publish /p:Version=$Version | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed'
}

$assembly = Join-Path $publish 'Jellyfin.Plugin.XtreamPostProcessor.dll'
if ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).FileVersion -ne $Version) {
    throw 'Assembly version does not match the requested package version'
}

Compress-Archive -Path $assembly -DestinationPath $archive
$checksum = (Get-FileHash $archive -Algorithm MD5).Hash.ToLowerInvariant()

[pscustomobject]@{
    Version = $Version
    Archive = $archive
    Checksum = $checksum
    SHA256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json
