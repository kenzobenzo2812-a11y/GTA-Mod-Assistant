# ============================================================================
# GTA Mod Assistant - Native Windows Build & Packaging Script (PowerShell)
# Usage: .\scripts\build-windows.ps1
# ============================================================================
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "[1/4] Restoring & running automated xUnit test suite..." -ForegroundColor Cyan
dotnet test "$RepoRoot\tests\GTAModAssistant.Tests\GTAModAssistant.Tests.csproj" -c Release

$OutDir = Join-Path (Split-Path -Parent $RepoRoot) "releases"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "[2/4] Publishing Self-Contained Single-File Portable Windows x64 Executable..." -ForegroundColor Cyan
$PortableStage = Join-Path $env:TEMP "gtama_portable_publish"
if (Test-Path $PortableStage) { Remove-Item -Recurse -Force $PortableStage }

dotnet publish "$RepoRoot\src\GTAModAssistant.App\GTAModAssistant.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $PortableStage

Copy-Item "$PortableStage\GTAModAssistant.exe" "$OutDir\GTAModAssistant-Portable-win-x64.exe" -Force

Write-Host "[3/4] Publishing CLI engine..." -ForegroundColor Cyan
dotnet publish "$RepoRoot\src\GTAModAssistant.Cli\GTAModAssistant.Cli.csproj" `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
    -o $PortableStage

if (Get-Command makensis -ErrorAction SilentlyContinue) {
    Write-Host "[4/4] Building NSIS Setup Installer..." -ForegroundColor Cyan
    makensis `
        "/DPUBLISH_DIR=$PortableStage" `
        "/DAPP_ICON_PATH=$RepoRoot\src\GTAModAssistant.App\Assets\app.ico" `
        "/DOUTPUT_SETUP_EXE=$OutDir\GTAModAssistant-Setup-win-x64.exe" `
        "$RepoRoot\installer\GTAModAssistant.nsi"
} else {
    Write-Host "[4/4] NSIS (makensis) not installed locally; Portable EXE generated at: $OutDir\GTAModAssistant-Portable-win-x64.exe" -ForegroundColor Yellow
}

Write-Host "Done! Deliverables ready in: $OutDir" -ForegroundColor Green
