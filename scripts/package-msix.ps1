#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.1.0',
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$IdentityName = 'keikawa.GDSPreviewforWindowsExplorer',
    [string]$Publisher = 'CN=915278F7-D39C-4A79-8E88-5A30F45250CB',
    [string]$PublisherDisplayName = 'keikawa',
    [string]$CertificateThumbprint,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$handlerClsid = '87F8A6BB-6B13-4A41-9D54-EEB39DBD1D6E'
$versionParts = $Version.Split('.')
if ($versionParts.Count -ne 4 -or ($versionParts | Where-Object { [uint32]$_ -gt 65535 })) {
    throw 'Each part of an MSIX version must be between 0 and 65535.'
}

function Get-ProjectTool([string]$localPath, [string]$commandName, [string]$errorMessage) {
    if (Test-Path -LiteralPath $localPath -PathType Leaf) { return $localPath }
    $command = Get-Command $commandName -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw $errorMessage
}

function Get-WindowsSdkTool([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $cachedToolsRoot = Join-Path $repoRoot '.codex-tmp\windows-sdk-build-tools\bin'
    if (Test-Path -LiteralPath $cachedToolsRoot) {
        $cachedCandidate = Get-ChildItem -LiteralPath $cachedToolsRoot -Directory |
            Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$name" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($cachedCandidate) { return $cachedCandidate }
    }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$name" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    throw "$name was not found. Install the Windows 10/11 SDK from https://developer.microsoft.com/windows/downloads/windows-sdk/."
}

function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

function New-PackageLogo([string]$path, [int]$width, [int]$height, [bool]$includeText) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::FromArgb(255, 17, 24, 39))
        $scale = [Math]::Min($width, $height)
        $margin = [Math]::Max(3, [int]($scale * 0.16))
        $lineWidth = [Math]::Max(2, [int]($scale * 0.065))
        $cyan = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 56, 189, 248), $lineWidth)
        $green = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 52, 211, 153), $lineWidth)
        try {
            $cyan.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $green.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $iconLeft = if ($width -gt $height) { $margin } else { $margin }
            $iconSize = $scale - 2 * $margin
            $graphics.DrawRectangle($cyan, $iconLeft, $margin, $iconSize, $iconSize)
            $offset = [Math]::Max(3, [int]($iconSize * 0.22))
            $inner = $iconSize - $offset * 2
            $graphics.DrawRectangle($green, $iconLeft + $offset, $margin + $offset, $inner, $inner)
            $graphics.DrawLine($green, $iconLeft + $offset, $margin + $iconSize - $offset, $iconLeft + $iconSize - $offset, $margin + $offset)
        } finally {
            $cyan.Dispose()
            $green.Dispose()
        }

        if ($includeText -and $width -gt $height) {
            $fontSize = [Math]::Max(12, [single]($height * 0.19))
            $font = [System.Drawing.Font]::new('Segoe UI Semibold', $fontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
            try {
                $textX = $scale + [int]($height * 0.12)
                $textRect = [System.Drawing.RectangleF]::new($textX, 0, $width - $textX, $height)
                $format = [System.Drawing.StringFormat]::new()
                try {
                    $format.Alignment = [System.Drawing.StringAlignment]::Near
                    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
                    $graphics.DrawString("GDS`nPreview", $font, $brush, $textRect, $format)
                } finally { $format.Dispose() }
            } finally {
                $font.Dispose()
                $brush.Dispose()
            }
        }
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$localDotnet = Join-Path $repoRoot '.codex-tmp\dotnet-sdk\dotnet.exe'
$dotnet = Get-ProjectTool $localDotnet 'dotnet' '.NET 8 SDK is required.'
$localZig = Join-Path $repoRoot '.codex-tmp\zig\zig-x86_64-windows-0.15.2\zig.exe'
$zig = Get-ProjectTool $localZig 'zig' 'Zig 0.15 or newer is required.'
$makeAppx = Get-WindowsSdkTool 'makeappx.exe'
$signTool = if ($CertificateThumbprint) { Get-WindowsSdkTool 'signtool.exe' } else { $null }

$taskCache = Join-Path $repoRoot '.codex-tmp\build-cache'
New-Item -ItemType Directory -Force -Path $taskCache | Out-Null
$env:DOTNET_CLI_HOME = $taskCache
$env:NUGET_PACKAGES = Join-Path $taskCache 'nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:ZIG_GLOBAL_CACHE_DIR = (New-Item -ItemType Directory -Force (Join-Path $repoRoot '.codex-tmp\zig-global-cache')).FullName
$env:ZIG_LOCAL_CACHE_DIR = (New-Item -ItemType Directory -Force (Join-Path $repoRoot '.codex-tmp\zig-local-cache')).FullName

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $configuration
    if ($LASTEXITCODE -ne 0) { throw 'Base build failed.' }
}

$nativeDll = Join-Path $repoRoot 'artifacts\GdsPreview\GdsPreview.Native.dll'
if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
    throw 'GdsPreview.Native.dll is missing. Run scripts\build.ps1 first or omit -SkipBuild.'
}

$msixRoot = Join-Path $repoRoot 'artifacts\msix'
$staging = Join-Path $msixRoot 'staging'
$rendererPublish = Join-Path $repoRoot '.codex-tmp\msix-renderer'
$verificationDirectory = Join-Path $repoRoot '.codex-tmp\msix-verification'
$packagePath = Join-Path $msixRoot "GDS-Preview-for-Windows-Explorer-$Version-x64.msix"
$checksumPath = Join-Path $msixRoot 'SHA256SUMS.txt'
foreach ($directory in $staging, $rendererPublish, $verificationDirectory) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    New-Item -ItemType Directory -Path $directory | Out-Null
}
New-Item -ItemType Directory -Force -Path (Join-Path $staging 'Assets') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $staging 'Samples') | Out-Null
if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }

$rendererProject = Join-Path $repoRoot 'src\GdsPreview.Renderer\GdsPreview.Renderer.csproj'
$configFile = Join-Path $repoRoot 'packaging\NuGet.Msix.Config'
& $dotnet restore $rendererProject --configfile $configFile -p:Platform=x64 -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'Self-contained renderer restore failed.' }
& $dotnet publish $rendererProject -c $configuration -p:Platform=x64 -r win-x64 --self-contained true --no-restore `
    -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$repoRoot=/_/gds-preview" -o $rendererPublish
if ($LASTEXITCODE -ne 0) { throw 'Self-contained renderer publish failed.' }

Get-ChildItem -LiteralPath $rendererPublish -File |
    Where-Object Extension -NotIn '.pdb', '.xml' |
    Copy-Item -Destination $staging
Copy-Item -LiteralPath $nativeDll -Destination $staging
Copy-Item -LiteralPath (Join-Path $repoRoot 'samples\demo.gds') -Destination (Join-Path $staging 'Samples')

$launcherSource = Join-Path $repoRoot 'native\GdsPreview.App.cpp'
$launcher = Join-Path $staging 'GdsPreview.App.exe'
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode $launcherSource -o $launcher -lshell32 -luser32
if ($LASTEXITCODE -ne 0) { throw 'MSIX launcher build failed.' }
Remove-Item -LiteralPath ([System.IO.Path]::ChangeExtension($launcher, '.pdb')) -Force -ErrorAction SilentlyContinue

New-PackageLogo (Join-Path $staging 'Assets\StoreLogo.png') 50 50 $false
New-PackageLogo (Join-Path $staging 'Assets\Square44x44Logo.png') 44 44 $false
New-PackageLogo (Join-Path $staging 'Assets\Square150x150Logo.png') 150 150 $false
New-PackageLogo (Join-Path $staging 'Assets\Wide310x150Logo.png') 310 150 $true

$manifestTemplate = Get-Content -LiteralPath (Join-Path $repoRoot 'packaging\AppxManifest.xml.template') -Raw
$manifestText = $manifestTemplate.Replace('@@IDENTITY_NAME@@', (Escape-Xml $IdentityName))
$manifestText = $manifestText.Replace('@@PUBLISHER@@', (Escape-Xml $Publisher))
$manifestText = $manifestText.Replace('@@VERSION@@', $Version)
$manifestText = $manifestText.Replace('@@PUBLISHER_DISPLAY_NAME@@', (Escape-Xml $PublisherDisplayName))
$manifestPath = Join-Path $staging 'AppxManifest.xml'
[System.IO.File]::WriteAllText($manifestPath, $manifestText, [System.Text.UTF8Encoding]::new($false))

& (Join-Path $PSScriptRoot 'verify-msix-manifest.ps1') -ManifestPath $manifestPath -VerifyPayload
if ($LASTEXITCODE -ne 0) { throw 'MSIX manifest verification failed.' }

$makeAppxOutput = & $makeAppx pack /o /d $staging /p $packagePath 2>&1
if ($LASTEXITCODE -ne 0) {
    $makeAppxOutput | Write-Host
    throw 'MakeAppx failed to create the MSIX package.'
}
Write-Host 'MakeAppx package validation passed.' -ForegroundColor Green
$makeAppxOutput = & $makeAppx unpack /o /p $packagePath /d $verificationDirectory 2>&1
if ($LASTEXITCODE -ne 0) {
    $makeAppxOutput | Write-Host
    throw 'MakeAppx failed to unpack the verification copy.'
}
& (Join-Path $PSScriptRoot 'verify-msix-manifest.ps1') `
    -ManifestPath (Join-Path $verificationDirectory 'AppxManifest.xml') -VerifyPayload
if ($LASTEXITCODE -ne 0) { throw 'Packed MSIX verification failed.' }

if ($CertificateThumbprint) {
    & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'SignTool failed to sign the MSIX package.' }
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($checksumPath, "$hash  $([System.IO.Path]::GetFileName($packagePath))`r`n", [System.Text.Encoding]::ASCII)

Write-Host "MSIX package: $packagePath" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor Green
Write-Host $(if ($CertificateThumbprint) { 'Package signed for sideload testing.' } else { 'Package is unsigned and intended for Partner Center upload.' }) -ForegroundColor Cyan
