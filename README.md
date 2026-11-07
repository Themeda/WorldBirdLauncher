**English** | [简体中文](README.zh-CN.md)

# WorldBirdLauncher

WorldBirdLauncher (WBL) is a Minecraft server browser and launcher client for Windows 10 and 11.
This repository contains the **0.2.0** WorldBirdLauncher client source snapshot dated **2026-11-07**
and is licensed under the MIT License.

## Current client features

- Browse, search, filter, and sort Minecraft servers from the server-delivered catalog.
- Display server status, MOTD, connection routes, mod loaders, versions, and listing information.
- Support local latency testing, favorites, play history, and full-size server-detail images.
- **Microsoft accounts are the only supported authentication method.** All other account types have been removed.
- Microsoft login uses the authorization code flow with PKCE through the system browser and an ephemeral loopback callback.
- Servers that declare an external account requirement remain visible, but WBL clearly blocks launching them.
- Support Java detection, version isolation, integrity checks, game launching, and direct server join.
- Support installing Fabric, Quilt, Forge, NeoForge, and OptiFine.
- Support modpack imports, mod search and downloads, and a resource download queue.
- Support light, dark, and system-following themes, plus multiple accounts.

## Requirements

- Windows 10 version 1809 or later; Windows 11 is recommended.
- .NET SDK 8.0.
- Visual Studio 2022 is optional but provides a better experience with the WPF workload.
- Microsoft Edge WebView2 Runtime is required for the 3D skin page.
- The Microsoft app registration must contain the **Mobile and desktop applications** redirect URI
  `http://localhost/callback`. Microsoft treats the loopback port dynamically.

The client targets both `net8.0-windows` and `net48`. Official distribution uses the project's custom net48 single-file packaging.

## Build

Run the following from the repository root:

```powershell
dotnet build WBL.sln
```

## Test

```powershell
dotnet test tests\WorldBirdLauncher.Core.Tests\WorldBirdLauncher.Core.Tests.csproj
```

## Create the net48 single-file executable

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pack-net48.ps1 -Out publish\wbl-0.2.0
```

Output:

```text
publish\wbl-0.2.0\WBL.exe
```

The single distribution target is `WBL.exe`. The packaging script appends the compressed runtime
dependencies to the executable, and the built-in AssemblyResolve entry point loads them at startup.
The icon source is `src/WorldBirdLauncher.App/Assets/app-icon.svg`; the application icon contains
16, 24, 32, 48, 64, 128, and 256 pixel variants.

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
