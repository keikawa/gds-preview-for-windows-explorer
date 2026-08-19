#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $repoRoot 'artifacts\GdsPreview'
$releaseDirectory = Join-Path $repoRoot 'artifacts\release'
$packageName = "GDS-Preview-for-Windows-Explorer-$Version-x64"
$stagingDirectory = Join-Path $releaseDirectory $packageName
$archivePath = Join-Path $releaseDirectory "$packageName.zip"
$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'

$requiredBinaries = @(
    'GdsPreview.Native.dll',
    'GdsPreview.Renderer.exe',
    'GdsPreview.Renderer.dll',
    'GdsPreview.Renderer.deps.json',
    'GdsPreview.Renderer.runtimeconfig.json',
    'GdsPreview.Core.dll'
)
foreach ($file in $requiredBinaries) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildDirectory $file) -PathType Leaf)) {
        throw "Missing build output '$file'. Run scripts\build.ps1 first."
    }
}

New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

foreach ($file in $requiredBinaries) {
    Copy-Item -LiteralPath (Join-Path $buildDirectory $file) -Destination $stagingDirectory
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\install.ps1') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\uninstall.ps1') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'install.cmd') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'uninstall.cmd') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'samples\demo.gds') -Destination $stagingDirectory
Set-Content -LiteralPath (Join-Path $stagingDirectory 'VERSION') -Value $Version -Encoding ascii

Compress-Archive -LiteralPath $stagingDirectory -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$hash  $packageName.zip" -Encoding ascii

Write-Host "Release package: $archivePath" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor Green
