#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('CurrentUser', 'Machine')]
    [string]$Scope = 'CurrentUser',
    [string]$InstallDirectory
)

$ErrorActionPreference = 'Stop'
$expectedDirectory = if ($Scope -eq 'Machine') {
    Join-Path $env:ProgramFiles 'GdsPreview'
} else {
    Join-Path $env:LOCALAPPDATA 'GdsPreview'
}
if (-not $InstallDirectory) { $InstallDirectory = $expectedDirectory }
$InstallDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
$expectedDirectory = [System.IO.Path]::GetFullPath($expectedDirectory)
if (-not $InstallDirectory.Equals($expectedDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "For safety, only the default install directory can be removed: $expectedDirectory"
}

$handlerClsid = '{87F8A6BB-6B13-4A41-9D54-EEB39DBD1D6E}'
$appId = '{105B23B8-9A22-4D54-94E7-57DD03B8BA8B}'
$previewShellExtension = '{8895B1C6-B41F-4C1C-A562-0D564250836F}'
$classesRoot = if ($Scope -eq 'Machine') { 'HKLM:\Software\Classes' } else { 'HKCU:\Software\Classes' }
$windowsRoot = if ($Scope -eq 'Machine') { 'HKLM:\Software\Microsoft\Windows\CurrentVersion' } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion' }
$previewHandlersKey = "$windowsRoot\PreviewHandlers"
$approvedKey = "$windowsRoot\Shell Extensions\Approved"

foreach ($extension in '.gds', '.gdsii') {
    $extensionKey = "$classesRoot\$extension\ShellEx\$previewShellExtension"
    if (Test-Path -LiteralPath $extensionKey) { Remove-Item -LiteralPath $extensionKey -Force }
}

if (Test-Path -LiteralPath $previewHandlersKey) {
    Remove-ItemProperty -LiteralPath $previewHandlersKey -Name $handlerClsid -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $approvedKey) {
    Remove-ItemProperty -LiteralPath $approvedKey -Name $handlerClsid -ErrorAction SilentlyContinue
}

$classKey = "$classesRoot\CLSID\$handlerClsid"
$appIdKey = "$classesRoot\AppID\$appId"
if (Test-Path -LiteralPath $classKey) { Remove-Item -LiteralPath $classKey -Recurse -Force }
if (Test-Path -LiteralPath $appIdKey) { Remove-Item -LiteralPath $appIdKey -Recurse -Force }

if (Test-Path -LiteralPath $InstallDirectory) {
    Remove-Item -LiteralPath $InstallDirectory -Recurse -Force
}

Write-Host 'GDS Preview for Windows Explorer was removed.' -ForegroundColor Green
