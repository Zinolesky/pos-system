$ErrorActionPreference = "Stop"
$dotnetExe = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnetExe)) {
    $dotnetExe = "dotnet"
}
$env:DOTNET_ROOT = "C:\Program Files\dotnet"
$env:NUGET_PACKAGES = "D:\Users\.nuget\packages"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"

Write-Host "1. Publishing self-contained win-x64 release (ReadyToRun native codegen) for PosSystem.Frontend..." -ForegroundColor Cyan
& $dotnetExe publish "$PSScriptRoot\src\PosSystem.Frontend\PosSystem.Frontend.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true

$isccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $isccPath) {
    Write-Host "2. Compiling Inno Setup Installer (installer.iss)..." -ForegroundColor Cyan
    & $isccPath "$PSScriptRoot\installer.iss"
    Write-Host "Installer generated at .\Installer\PrimePOS_Setup_v2.exe" -ForegroundColor Green
} else {
    Write-Host "2. Inno Setup 6 (ISCC.exe) not found at '$isccPath'. Self-contained binaries are ready at:" -ForegroundColor Yellow
    Write-Host "   src\PosSystem.Frontend\bin\Release\net8.0-windows\win-x64\publish\" -ForegroundColor Green
}
