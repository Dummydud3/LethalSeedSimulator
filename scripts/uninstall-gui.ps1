$ErrorActionPreference = "Stop"

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\LethalSeedSimulator"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\LethalSeedSimulator"

if (Test-Path $installRoot) {
    Remove-Item $installRoot -Recurse -Force
}

if (Test-Path $startMenuDir) {
    Remove-Item $startMenuDir -Recurse -Force
}

Write-Host "Uninstalled Lethal Seed Simulator."
