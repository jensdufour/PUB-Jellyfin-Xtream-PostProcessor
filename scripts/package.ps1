param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Jellyfin.Plugin.XtreamPostProcessor/Jellyfin.Plugin.XtreamPostProcessor.csproj'
$output = Join-Path $root 'dist'
$publish = Join-Path $output 'publish'
$archive = Join-Path $output "xtream-post-processor_$Version.zip"

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item $publish -ItemType Directory -Force | Out-Null

dotnet publish $project --configuration Release --output $publish /p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed'
}

Compress-Archive -Path (Join-Path $publish 'Jellyfin.Plugin.XtreamPostProcessor.dll') -DestinationPath $archive
$checksum = (Get-FileHash $archive -Algorithm MD5).Hash.ToLowerInvariant()

[pscustomobject]@{
    Version = $Version
    Archive = $archive
    Checksum = $checksum
} | ConvertTo-Json
