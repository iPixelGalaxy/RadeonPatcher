# RadeonPatcher

RadeonPatcher is an independent Windows utility for downloading, patching, signing, and installing Radeon display and audio drivers.

This project is not affiliated with, endorsed by, sponsored by, or supported by AMD. AMD, Radeon, Adrenalin, and related names are trademarks of Advanced Micro Devices, Inc.

## Features

- Detects the installed Radeon GPU and current display/audio driver versions.
- Finds available AMD driver packages from the mapped AMD support page.
- Supports selecting older driver versions when AMD exposes previous-driver downloads.
- Installs the display driver INF directly.
- Optionally applies Windows Server compatibility patches before driver installation.
- Optionally installs AMD Software: Adrenalin Edition from the downloaded package.
- Optionally installs the bundled AMD HD Audio Driver 10.0.1.42.
- Optionally disables the Windows MPO registry override.
- Optionally installs a scheduled update checker that runs at Windows boot and every 24 hours.
- Follows the system light/dark theme, with manual System, Light, and Dark choices.

## Update Checker

The `Install Update Check Service` option registers a Windows Scheduled Task named `RadeonPatcher Update Check`.

The task runs the same executable with:

```powershell
RadeonPatcher.exe --check-updates
```

If a newer driver is found, RadeonPatcher plays the Windows notification sound and shows a tray notification. It does not download or install drivers automatically.

## Build

Requirements:

- Windows
- .NET SDK targeting `net10.0-windows`

Build the solution:

```powershell
dotnet build .\RadeonPatcher.sln -c Release
```

Publish a self-contained single-file executable:

```powershell
dotnet publish .\RadeonPatcher\RadeonPatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

The published executable is named `RadeonPatcher.exe`.

## Notes

RadeonPatcher performs driver installation and scheduled-task registration operations that require administrator rights. Review the selected options before installing drivers.
