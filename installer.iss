[Setup]
AppName=Prime Supermarket Hybrid POS
AppVersion=2.0
DefaultDirName={autopf}\PrimePOS
DefaultGroupName=Prime POS
PrivilegesRequired=lowest
OutputDir=.\Installer
OutputBaseFilename=PrimePOS_Setup_v2

[Files]
Source: "src\PosSystem.Frontend\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Prime POS Terminal"; Filename: "{app}\PosSystem.Frontend.exe"
Name: "{autodesktop}\Prime POS Terminal"; Filename: "{app}\PosSystem.Frontend.exe"
