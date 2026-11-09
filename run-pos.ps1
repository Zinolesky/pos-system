$ErrorActionPreference = "Stop"
$dotnetExe = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnetExe)) {
    $dotnetExe = "dotnet"
}
$env:DOTNET_ROOT = "C:\Program Files\dotnet"
$env:NUGET_PACKAGES = "D:\Users\.nuget\packages"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"

$releaseExe = "$PSScriptRoot\src\PosSystem.Frontend\bin\Release\net8.0-windows\PosSystem.Frontend.exe"
$debugExe = "$PSScriptRoot\src\PosSystem.Frontend\bin\Debug\net8.0-windows\PosSystem.Frontend.exe"

if (Test-Path $releaseExe) {
    Write-Host "Launching Prime Supermarket POS v2.1 (Release Optimized)..." -ForegroundColor Green
    Start-Process -FilePath $releaseExe -WorkingDirectory (Split-Path $releaseExe)
} elseif (Test-Path $debugExe) {
    Write-Host "Launching Prime Supermarket POS v2.1..." -ForegroundColor Green
    Start-Process -FilePath $debugExe -WorkingDirectory (Split-Path $debugExe)
} else {
    Write-Host "Building & Launching Prime Supermarket POS v2.1 (Release)..." -ForegroundColor Cyan
    & $dotnetExe run -c Release --project "$PSScriptRoot\src\PosSystem.Frontend\PosSystem.Frontend.csproj"
}
