$ErrorActionPreference = "Stop"
$dotnetExe = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnetExe)) {
    $dotnetExe = "dotnet"
}
$env:DOTNET_ROOT = "C:\Program Files\dotnet"
$env:NUGET_PACKAGES = "D:\Users\.nuget\packages"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"

& $dotnetExe run -c Release --project "$PSScriptRoot\src\PosSystem.Tests\PosSystem.Tests.csproj"
