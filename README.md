**English** | [简体中文](README.zh-CN.md)

# WorldBirdLauncher

WorldBirdLauncher (WBL) is a Minecraft server browser and launcher client for Windows 10 and 11.
This repository contains a source snapshot of the WorldBirdLauncher client dated **2026-10-24** and is licensed under the MIT License.

## Current client features

- Browse, search, filter, and sort Minecraft servers from the server-delivered catalog.
- Display server status, MOTD, connection routes, mod loaders, versions, and listing information.
- Support local latency testing, favorites, play history, and full-size server-detail images.
- Support Microsoft, external Yggdrasil, and UnifiedPass login; the current public edition does not provide offline login.
- Support Java detection, version isolation, integrity checks, game launching, and direct server join.
- Support installing Fabric, Quilt, Forge, NeoForge, and OptiFine.
- Support modpack imports, mod search and downloads, and a resource download queue.
- Support light, dark, and system-following themes, plus multiple accounts.

## Requirements

- Windows 10 version 1809 or later; Windows 11 is recommended.
- .NET SDK 8.0.
- Visual Studio 2022 is optional but provides a better experience with the WPF workload.
- Microsoft Edge WebView2 Runtime is required for the 3D skin page.

The client targets both `net8.0-windows` and `net48`. Official distribution uses the project's custom net48 single-file packaging.

## Build

Run the following from the repository root:

```powershell
dotnet build WBL.sln
```

To build only the current public edition:

```powershell
dotnet build src\WorldBirdLauncher.App\WorldBirdLauncher.App.csproj -p:LiteEdition=true
```

## Test

```powershell
dotnet test tests\WorldBirdLauncher.Core.Tests\WorldBirdLauncher.Core.Tests.csproj
```

## Create the net48 single-file executable

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pack-net48.ps1 -Lite -Out publish\_staging-lite
```

Output:

```text
publish\_staging-lite\WBL.exe
```

The packaging script appends the compressed runtime dependencies to the end of the executable. At startup, the built-in AssemblyResolve entry point loads them.

## Project structure

```text
WBL.sln
src/
  WorldBirdLauncher.App/       WPF client
  WorldBirdLauncher.Core/      Launcher core logic
  WorldBirdLauncher.Shared/    Platform-neutral models, protocols, and compatibility layer
tests/
  WorldBirdLauncher.Core.Tests/
scripts/
  pack-net48.ps1
```

## Third-party components

The client includes WebView2 and `skinview3d`. Their license information is retained alongside the corresponding resource files.

## License

This project is licensed under the [MIT License](LICENSE).
