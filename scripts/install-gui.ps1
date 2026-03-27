$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptRoot
$projectFile = Join-Path $projectRoot "src\LethalSeedSimulator.Gui\LethalSeedSimulator.Gui.csproj"

$publishDir = Join-Path $projectRoot "artifacts\gui-publish"
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\LethalSeedSimulator"
$exePath = Join-Path $installRoot "LethalSeedSimulator.Gui.exe"

dotnet publish $projectFile -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Copy-Item (Join-Path $publishDir "*") $installRoot -Force

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\LethalSeedSimulator"
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null

$shortcutPath = Join-Path $startMenuDir "Lethal Seed Simulator.lnk"
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installRoot
$shortcut.Save()

Write-Host "Installed Lethal Seed Simulator to $installRoot"
Write-Host "Start menu shortcut: $shortcutPath"
