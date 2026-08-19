#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$hostExecutable = Join-Path $repoRoot 'artifacts\NativeSmoke.exe'
$sample = Join-Path $repoRoot 'samples\demo.gds'
$output = Join-Path $repoRoot 'artifacts\registered-preview.bmp'
if (-not (Test-Path -LiteralPath $hostExecutable)) { throw 'Run scripts\build.ps1 first.' }
& $hostExecutable --registered $sample $output 8000
if ($LASTEXITCODE -ne 0) { throw "Registered preview test failed with exit code $LASTEXITCODE." }
Write-Host "Registered cross-process preview succeeded: $output" -ForegroundColor Green
