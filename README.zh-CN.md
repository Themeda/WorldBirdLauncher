[English](README.md) | **简体中文**

# MCSL

MCSL（Minecraft Server List Launcher）是一个面向 Windows 10/11 的 Minecraft 服务器列表与启动器客户端。
仓库内容为 **2026-10-24 的 MCSL 客户端源码快照**，使用 MIT License。

## 当前客户端能力

- 从服务端目录浏览、搜索、筛选和排序 Minecraft 服务器。
- 显示服务器状态、MOTD、线路、加载器、版本与收录信息。
- 支持本机延迟测试、收藏、历史记录和服务器详情大图。
- 支持微软正版、外置 Yggdrasil、统一通行证登录；Lite 版不提供离线登录。
- 支持 Java 检测、版本隔离、完整性检查、游戏启动和直连进服。
- 支持 Fabric、Quilt、Forge、NeoForge 与 OptiFine 安装。
- 支持整合包导入、模组搜索与下载，以及资源下载队列。
- 支持浅色、深色、跟随系统主题和多账号管理。

## 环境要求

- Windows 10 1809 或更高版本，推荐 Windows 11。
- .NET SDK 8.0。
- Visual Studio 2022（可选，使用 WPF 工作负载时体验更完整）。
- 运行 3D 皮肤页需要 Microsoft Edge WebView2 Runtime。

客户端同时面向 `net8.0-windows` 和 `net48`。正式发布路径为自研的 net48 单文件打包。

## 构建

在仓库根目录执行：

```powershell
dotnet build MCSL.sln
```

仅构建 Lite 版：

```powershell
dotnet build src\McServerList.App\McServerList.App.csproj -p:LiteEdition=true
```

## 测试

```powershell
dotnet test tests\McServerList.Core.Tests\McServerList.Core.Tests.csproj
```

## 生成 net48 单文件

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pack-net48.ps1 -Lite -Out publish\_staging-lite
```

产物：

```text
publish\_staging-lite\MCSL-Lite.exe
```

打包脚本会把运行时依赖压缩后附加到 exe 尾部，启动时由程序内置的 AssemblyResolve 入口加载。

## 项目结构

```text
MCSL.sln
src/
  McServerList.App/       WPF 客户端
  McServerList.Core/      启动器核心逻辑
  McServerList.Shared/    平台无关模型、协议与兼容层
tests/
  McServerList.Core.Tests/
scripts/
  pack-net48.ps1
```

## 第三方组件

客户端包含 WebView2 与 `skinview3d`。相关许可信息随对应资源文件保留。

## 许可证

本项目使用 [MIT License](LICENSE)。
