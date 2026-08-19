[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $repoRoot '.codex-tmp\dotnet-sdk\dotnet.exe'
$dotnet = $null

if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        $sdks = & $command.Source --list-sdks
        if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $command.Source }
    }
}

if (-not $dotnet) {
    throw '.NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0'
}

$taskCache = Join-Path $repoRoot '.codex-tmp\build-cache'
New-Item -ItemType Directory -Force -Path $taskCache | Out-Null
$env:DOTNET_CLI_HOME = $taskCache
$env:NUGET_PACKAGES = Join-Path $taskCache 'nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$configFile = Join-Path $repoRoot 'NuGet.Config'
$testProject = Join-Path $repoRoot 'tests\GdsPreview.Core.Tests\GdsPreview.Core.Tests.csproj'
$sampleProject = Join-Path $repoRoot 'tools\GdsPreview.Sample\GdsPreview.Sample.csproj'
$rendererProject = Join-Path $repoRoot 'src\GdsPreview.Renderer\GdsPreview.Renderer.csproj'
$publishDirectory = Join-Path $repoRoot 'artifacts\GdsPreview'
$sampleFile = Join-Path $repoRoot 'samples\demo.gds'
$smokeImage = Join-Path $repoRoot 'artifacts\native-preview.bmp'
$initialResizeImage = Join-Path $repoRoot 'artifacts\initial-resize-preview.bmp'
$nativeSource = Join-Path $repoRoot 'native\GdsPreview.Native.cpp'
$nativeDefinition = Join-Path $repoRoot 'native\GdsPreview.Native.def'
$nativeSmokeSource = Join-Path $repoRoot 'native\NativeSmoke.cpp'
$hangRendererSource = Join-Path $repoRoot 'native\HangRenderer.cpp'
$launcherSource = Join-Path $repoRoot 'native\GdsPreview.App.cpp'
$nativeDll = Join-Path $publishDirectory 'GdsPreview.Native.dll'
$launcher = Join-Path $publishDirectory 'GdsPreview.App.exe'
$nativeSmoke = Join-Path $repoRoot 'artifacts\NativeSmoke.exe'
$localZig = Join-Path $repoRoot '.codex-tmp\zig\zig-x86_64-windows-0.15.2\zig.exe'
$zig = if (Test-Path -LiteralPath $localZig) { $localZig } else {
    $zigCommand = Get-Command zig -ErrorAction SilentlyContinue
    if ($zigCommand) { $zigCommand.Source } else { $null }
}
if (-not $zig) { throw 'Zig 0.15 or newer is required to build the native preview DLL: https://ziglang.org/download/' }

& $dotnet restore $testProject --configfile $configFile
if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
& $dotnet run --project $testProject -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

& $dotnet restore $sampleProject --configfile $configFile
if ($LASTEXITCODE -ne 0) { throw 'Sample restore failed.' }
& $dotnet run --project $sampleProject -c $Configuration --no-restore -- $sampleFile
if ($LASTEXITCODE -ne 0) { throw 'Sample generation failed.' }

New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
foreach ($oldFile in 'GdsPreview.Handler.comhost.dll','GdsPreview.Handler.dll','GdsPreview.Handler.deps.json','GdsPreview.Handler.runtimeconfig.json') {
    Remove-Item -LiteralPath (Join-Path $publishDirectory $oldFile) -Force -ErrorAction SilentlyContinue
}
& $dotnet restore $rendererProject --configfile $configFile -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Renderer restore failed.' }
& $dotnet publish $rendererProject -c $Configuration -p:Platform=x64 --no-restore --no-self-contained -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Renderer publish failed.' }

$env:ZIG_GLOBAL_CACHE_DIR = (New-Item -ItemType Directory -Force (Join-Path $repoRoot '.codex-tmp\zig-global-cache')).FullName
$env:ZIG_LOCAL_CACHE_DIR = (New-Item -ItemType Directory -Force (Join-Path $repoRoot '.codex-tmp\zig-local-cache')).FullName
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -shared $nativeSource $nativeDefinition -o $nativeDll -lole32 -luuid -luser32 -lgdi32
if ($LASTEXITCODE -ne 0) { throw 'Native handler build failed.' }
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode $launcherSource -o $launcher -lshell32 -luser32
if ($LASTEXITCODE -ne 0) { throw 'Native launcher build failed.' }
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode $nativeSmokeSource -o $nativeSmoke -lole32 -luuid -luser32 -lgdi32
if ($LASTEXITCODE -ne 0) { throw 'Native smoke-host build failed.' }
& $nativeSmoke $nativeDll $sampleFile $smokeImage 4000
if ($LASTEXITCODE -ne 0) { throw 'Native end-to-end preview test failed.' }
& $nativeSmoke --initial-resize $nativeDll $sampleFile $initialResizeImage 4000
if ($LASTEXITCODE -ne 0) { throw 'Initial resize preview regression test failed.' }

$timeoutDirectory = New-Item -ItemType Directory -Force (Join-Path $repoRoot '.codex-tmp\timeout-isolation')
$hangRenderer = Join-Path $timeoutDirectory.FullName 'GdsPreview.Renderer.exe'
$timeoutDll = Join-Path $timeoutDirectory.FullName 'GdsPreview.Native.dll'
$timeoutImage = Join-Path $repoRoot 'artifacts\timeout-preview.bmp'
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode $hangRendererSource -o $hangRenderer
if ($LASTEXITCODE -ne 0) { throw 'Timeout-test renderer build failed.' }
Copy-Item -LiteralPath $nativeDll -Destination $timeoutDll -Force
& $nativeSmoke $timeoutDll $sampleFile $timeoutImage 7500
if ($LASTEXITCODE -ne 0) { throw 'Renderer timeout-isolation test failed.' }
if (Get-Process -Name 'GdsPreview.Renderer' -ErrorAction SilentlyContinue) {
    throw 'The isolated renderer was not terminated after its timeout.'
}

Write-Host "Build complete: $publishDirectory" -ForegroundColor Green
Write-Host "Native isolation test image: $smokeImage" -ForegroundColor Green
Write-Host "Initial resize test image: $initialResizeImage" -ForegroundColor Green
Write-Host "Timeout isolation test image: $timeoutImage" -ForegroundColor Green
Write-Host "Install with: powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1" -ForegroundColor Cyan
