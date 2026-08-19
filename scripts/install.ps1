#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [ValidateSet('CurrentUser', 'Machine')]
    [string]$Scope = 'CurrentUser',
    [string]$InstallDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourceDirectory) {
    $SourceDirectory = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'GdsPreview.Native.dll')) {
        $PSScriptRoot
    } else {
        Join-Path $repoRoot 'artifacts\GdsPreview'
    }
}
if (-not $InstallDirectory) {
    $InstallDirectory = if ($Scope -eq 'Machine') {
        Join-Path $env:ProgramFiles 'GdsPreview'
    } else {
        Join-Path $env:LOCALAPPDATA 'GdsPreview'
    }
}
$SourceDirectory = [System.IO.Path]::GetFullPath($SourceDirectory)
$InstallDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)

if ($Scope -eq 'Machine') {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Machine scope installation requires an elevated PowerShell session.'
    }
}

$handlerClsid = '{87F8A6BB-6B13-4A41-9D54-EEB39DBD1D6E}'
$appId = '{105B23B8-9A22-4D54-94E7-57DD03B8BA8B}'
$previewShellExtension = '{8895B1C6-B41F-4C1C-A562-0D564250836F}'
$handlerName = 'GDS Preview for Windows Explorer'
$sourceNativeHandler = Join-Path $SourceDirectory 'GdsPreview.Native.dll'

if (-not (Test-Path -LiteralPath $sourceNativeHandler -PathType Leaf)) {
    throw "The built native COM DLL was not found. Run scripts\build.ps1 first: $sourceNativeHandler"
}

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
foreach ($oldFile in 'GdsPreview.Handler.comhost.dll','GdsPreview.Handler.dll','GdsPreview.Handler.deps.json','GdsPreview.Handler.runtimeconfig.json') {
    Remove-Item -LiteralPath (Join-Path $InstallDirectory $oldFile) -Force -ErrorAction SilentlyContinue
}
foreach ($file in 'GdsPreview.Native.dll','GdsPreview.Renderer.exe','GdsPreview.Renderer.dll','GdsPreview.Renderer.deps.json','GdsPreview.Renderer.runtimeconfig.json','GdsPreview.Core.dll') {
    Copy-Item -LiteralPath (Join-Path $SourceDirectory $file) -Destination $InstallDirectory -Force
}

$installedComHost = Join-Path $InstallDirectory 'GdsPreview.Native.dll'
$classesRoot = if ($Scope -eq 'Machine') { 'HKLM:\Software\Classes' } else { 'HKCU:\Software\Classes' }
$windowsRoot = if ($Scope -eq 'Machine') { 'HKLM:\Software\Microsoft\Windows\CurrentVersion' } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion' }
$classKey = "$classesRoot\CLSID\$handlerClsid"
$inprocKey = Join-Path $classKey 'InprocServer32'
$appIdKey = "$classesRoot\AppID\$appId"
$previewHandlersKey = "$windowsRoot\PreviewHandlers"
$approvedKey = "$windowsRoot\Shell Extensions\Approved"

New-Item -Path $classKey -Force | Out-Null
Set-Item -Path $classKey -Value $handlerName
New-ItemProperty -Path $classKey -Name 'AppID' -Value $appId -PropertyType String -Force | Out-Null
New-Item -Path $inprocKey -Force | Out-Null
Set-Item -Path $inprocKey -Value $installedComHost
New-ItemProperty -Path $inprocKey -Name 'ThreadingModel' -Value 'Apartment' -PropertyType String -Force | Out-Null

New-Item -Path $appIdKey -Force | Out-Null
Set-Item -Path $appIdKey -Value $handlerName
New-ItemProperty -Path $appIdKey -Name 'DllSurrogate' -Value 'Prevhost.exe' -PropertyType String -Force | Out-Null

New-Item -Path $previewHandlersKey -Force | Out-Null
New-ItemProperty -Path $previewHandlersKey -Name $handlerClsid -Value $handlerName -PropertyType String -Force | Out-Null
New-Item -Path $approvedKey -Force | Out-Null
New-ItemProperty -Path $approvedKey -Name $handlerClsid -Value $handlerName -PropertyType String -Force | Out-Null

foreach ($extension in '.gds', '.gdsii') {
    $extensionKey = "$classesRoot\$extension\ShellEx\$previewShellExtension"
    New-Item -Path $extensionKey -Force | Out-Null
    Set-Item -Path $extensionKey -Value $handlerClsid
}

Write-Host "Installed $handlerName ($Scope) to $InstallDirectory" -ForegroundColor Green
Write-Host 'Reopen the Explorer preview pane (Alt+P).' -ForegroundColor Cyan
