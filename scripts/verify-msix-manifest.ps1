#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManifestPath,
    [switch]$VerifyPayload
)

$ErrorActionPreference = 'Stop'
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "MSIX manifest not found: $ManifestPath"
}

[xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
$namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$namespaces.AddNamespace('desktop2', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/2')
$namespaces.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
$namespaces.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

function Require-Node([string]$xpath, [string]$description) {
    $node = $manifest.SelectSingleNode($xpath, $namespaces)
    if (-not $node) { throw "MSIX manifest is missing $description." }
    return $node
}

$identity = Require-Node '/f:Package/f:Identity' 'package identity'
if ($identity.ProcessorArchitecture -ne 'x64') { throw 'MSIX package must target x64.' }
if ($identity.Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'MSIX version must contain four numeric parts.' }

$application = Require-Node '/f:Package/f:Applications/f:Application' 'desktop application'
if ($application.Executable -ne 'GdsPreview.App.exe') { throw 'Unexpected MSIX application executable.' }
$targetDevice = Require-Node '/f:Package/f:Dependencies/f:TargetDeviceFamily[@Name="Windows.Desktop"]' 'Windows.Desktop target device family'
if ([version]$targetDevice.MinVersion -lt [version]'10.0.17763.0') {
    throw 'Microsoft Store MSIX packages must target Windows 10 version 1809 (10.0.17763.0) or later.'
}

$preview = Require-Node '//desktop2:DesktopPreviewHandler' 'desktop preview handler declaration'
$comClass = Require-Node '//com:SurrogateServer/com:Class' 'COM surrogate class declaration'
$surrogate = Require-Node '//com:SurrogateServer' 'COM preview surrogate declaration'
if ($preview.Clsid -ne $comClass.Id) { throw 'Preview handler CLSID does not match the COM class CLSID.' }
if ($surrogate.SystemSurrogate -ne 'PreviewHost') { throw 'COM class must use the PreviewHost system surrogate.' }
if ($comClass.ThreadingModel -ne 'STA') { throw 'Preview handler COM class must use the STA threading model.' }

$fileTypes = @($manifest.SelectNodes('//uap:SupportedFileTypes/uap:FileType', $namespaces) | ForEach-Object { $_.'#text' })
foreach ($extension in '.gds', '.gdsii') {
    if ($fileTypes -notcontains $extension) { throw "MSIX manifest does not register $extension." }
}
Require-Node '//rescap:Capability[@Name="runFullTrust"]' 'runFullTrust capability' | Out-Null

if ($VerifyPayload) {
    $payloadRoot = Split-Path -Parent $ManifestPath
    $requiredPaths = @(
        $application.Executable,
        $comClass.Path,
        'GdsPreview.Renderer.exe',
        'Samples\demo.gds',
        'Assets\StoreLogo.png',
        'Assets\Square44x44Logo.png',
        'Assets\Square150x150Logo.png',
        'Assets\Wide310x150Logo.png'
    )
    foreach ($relativePath in $requiredPaths) {
        if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $relativePath) -PathType Leaf)) {
            throw "MSIX payload is missing $relativePath."
        }
    }

    $debugFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter '*.pdb')
    if ($debugFiles.Count -gt 0) {
        throw "MSIX payload must not contain debug symbols: $($debugFiles.Name -join ', ')"
    }

    $allowedLayout = [System.IO.Path]::GetFullPath((Join-Path $payloadRoot 'Samples\demo.gds'))
    $unexpectedLayouts = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File |
        Where-Object { $_.Extension -in '.gds', '.gdsii', '.oas' -and $_.FullName -ne $allowedLayout })
    if ($unexpectedLayouts.Count -gt 0) {
        throw "MSIX payload contains an unexpected layout file: $($unexpectedLayouts.Name -join ', ')"
    }

    $allowedImages = @(
        'Assets\StoreLogo.png',
        'Assets\Square44x44Logo.png',
        'Assets\Square150x150Logo.png',
        'Assets\Wide310x150Logo.png'
    ) | ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $payloadRoot $_)) }
    $unexpectedImages = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File |
        Where-Object { $_.Extension -in '.png', '.jpg', '.jpeg', '.bmp', '.gif', '.tif', '.tiff' -and
            $_.FullName -notin $allowedImages })
    if ($unexpectedImages.Count -gt 0) {
        throw "MSIX payload contains an unexpected image file: $($unexpectedImages.Name -join ', ')"
    }
}

Write-Host "MSIX manifest verified: $ManifestPath" -ForegroundColor Green
