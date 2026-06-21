# RadeonPatcher

⚠️ Disclaimer: This project is coded with AI. I am determine to squash any bugs and am happy to take any feed back in issues.

⚠️ Currently only tested with the RX 9070 XT as I don't have any other GPUs, but this should work with any modern AMD GPU.

RadeonPatcher is an independent Windows utility for downloading, patching, signing, installing, and removing Radeon display, AMD HD Audio, and AMD Software packages.

This project is not affiliated with, endorsed by, sponsored by, or supported by AMD. AMD, Radeon, Adrenalin, and related names are trademarks of Advanced Micro Devices, Inc.

<img width="1049" height="769" alt="610828175-97ee4c78-933b-4669-9422-7d9ae0fe6ee7" src="https://github.com/user-attachments/assets/5399c755-0dc3-4b9b-b64b-7b8aeda4a967" />

## Features

- Detects AMD display hardware without requiring an existing AMD driver.
- Detects installed Radeon package and AMD HD Audio driver versions.
- Loads current and previous driver releases from the GPU's AMD support page.
- Updates, downgrades, or reinstalls the selected display driver.
- Patches and locally signs display and audio catalogs when compatibility changes are required.
- Adds Windows Server INF compatibility automatically on Server editions.
- Installs, updates, downgrades, reinstalls, or removes AMD Software: Adrenalin Edition.
- Backs up Adrenalin preferences before removal and restores them after installation.
- Installs or removes the AMD HD Audio driver independently.
- Removes the GPU driver, audio driver, AMD Software, or all components together.
- Toggles the Windows MPO override and reports its current state.
- Manages downloaded, extracted, and patched package caches with optional automatic cleanup.
- Remembers installation choices in the user's application data.
- Provides System, Light, and Dark themes.
- Offers a lightweight scheduled driver update checker for startup and daily checks.
- Checks the upstream GitHub Releases for RadeonPatcher updates and can replace and restart itself.

## Application Updates

At startup, RadeonPatcher checks the latest release from the upstream [`iPixelGalaxy/RadeonPatcher`](https://github.com/iPixelGalaxy/RadeonPatcher) repository. When a newer version exists, it displays the installed and available versions and offers **Maybe Later** or **Update**.

Updates are downloaded only from that repository's GitHub Release asset path. RadeonPatcher verifies `RadeonPatcher.exe` against its published SHA-256 file to detect download corruption. The upstream GitHub repository is the update trust anchor; the checksum is not an independent authenticity guarantee.

## Driver Update Checker

The **Install Update Check Service** button registers a non-elevated scheduled task named `RadeonPatcher Update Check`. The standalone checker runs its own lightweight hardware and AMD release lookup at Windows startup and once every 24 hours; it does not launch the administrator-privileged main application. New driver availability produces a Windows notification, and drivers are never installed automatically. Running `C:\ProgramData\RadeonPatcher\RadeonPatcherUpdateCheck.exe` directly performs a test check and reports the result in a notification.

## Verified Releases

Releases are produced by the manual **Build Release** GitHub Actions workflow on a clean `windows-latest` runner. The workflow accepts:

- `version`: release and executable version in `major.minor.patch` format, such as `1.2.0`.
- `branch`: branch, tag, or commit to check out and build.

The workflow publishes a self-contained `RadeonPatcher.exe`, generates its SHA-256 checksum, uploads both as workflow artifacts, and creates the corresponding GitHub Release from the exact checked-out commit.

## Local Build

Requirements:

- Windows
- .NET 10 SDK

```powershell
dotnet publish .\RadeonPatcher\RadeonPatcher.csproj -c Release -r win-x64 --self-contained true
```

The published executable is written to the repository root as `RadeonPatcher.exe`.

## Notes

RadeonPatcher requires administrator rights for driver installation, certificate trust, scheduled-task registration, and component removal. Review selected options before changing installed drivers.

Server compatibility patching creates a non-exportable, two-year local code-signing certificate named `AMD Driver Modding Authority`. Its trust is added only to the machine Root and TrustedPublisher stores. **Uninstall All** removes that certificate from the machine certificate stores.
