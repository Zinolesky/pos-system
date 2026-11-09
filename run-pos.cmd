@echo off
set "DOTNET_ROOT=C:\Program Files\dotnet"
set "NUGET_PACKAGES=D:\Users\.nuget\packages"
set "PATH=C:\Program Files\dotnet;%PATH%"

set "REL_EXE=%~dp0src\PosSystem.Frontend\bin\Release\net8.0-windows\PosSystem.Frontend.exe"
set "DBG_EXE=%~dp0src\PosSystem.Frontend\bin\Debug\net8.0-windows\PosSystem.Frontend.exe"

if exist "%REL_EXE%" (
    echo Launching Prime Supermarket POS v2.1 (Release Optimized)...
    start "" /D "%~dp0src\PosSystem.Frontend\bin\Release\net8.0-windows" "%REL_EXE%"
) else if exist "%DBG_EXE%" (
    echo Launching Prime Supermarket POS v2.1...
    start "" /D "%~dp0src\PosSystem.Frontend\bin\Debug\net8.0-windows" "%DBG_EXE%"
) else (
    echo Building and launching Prime Supermarket POS v2.1...
    "C:\Program Files\dotnet\dotnet.exe" run -c Release --project "%~dp0src\PosSystem.Frontend\PosSystem.Frontend.csproj"
)
