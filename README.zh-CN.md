[English](README.md) | **简体中文**

# WorldBirdLauncher

WorldBirdLauncher（WBL）是一个面向 Windows 10/11 的 Minecraft 服务器列表与启动器客户端。
仓库内容为 **2026-11-07 的 WorldBirdLauncher 0.2.0 客户端源码快照**，使用 MIT License。

## 当前客户端能力

- 从服务端目录浏览、搜索、筛选和排序 Minecraft 服务器。
- 显示服务器状态、MOTD、线路、加载器、版本与收录信息。
- 支持本机延迟测试、收藏、历史记录和服务器详情大图。
- **仅支持 Microsoft 账户认证。** 其他账户类型及相关入口均已移除。
- 微软登录使用系统浏览器、授权代码流、PKCE 和临时回环端口回调。
- 启动客户端时不会自动发起登录；只有用户打开账户管理后才进入正版授权流程。
- 声明需要外部账户的服务器仍会显示，但启动器会明确提示并阻止错误启动。
- 支持 Java 检测、版本隔离、完整性检查、游戏启动和直连进服。
- 识别并启动本机已安装的 Fabric、Quilt、Forge、NeoForge、OptiFine 与原版版本。
- 支持整合包导入、模组搜索与下载，以及资源下载队列。
- 支持浅色、深色、跟随系统主题和多账号管理。
- 支持可收起的图标导航栏、分组设置页和自定义主题强调色。
- 游戏版本需提前安装在本地；独立的游戏版本安装功能已移除。

## 环境要求

- Windows 10 1809 或更高版本，推荐 Windows 11。
- .NET SDK 8.0。
- Visual Studio 2022（可选，使用 WPF 工作负载时体验更完整）。
- 运行 3D 皮肤页需要 Microsoft Edge WebView2 Runtime。
- 微软应用注册需在“移动和桌面应用程序”平台添加重定向 URI
  `http://localhost/callback`；微软会忽略 localhost 回环地址的动态端口。

客户端同时面向 `net8.0-windows` 和 `net48`。正式发布路径为自研的 net48 单文件打包。

## 构建

在仓库根目录执行：

```powershell
dotnet build WBL.sln
```

## 测试

```powershell
dotnet test tests\WorldBirdLauncher.Core.Tests\WorldBirdLauncher.Core.Tests.csproj
```

## 生成 net48 单文件

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pack-net48.ps1 -Out publish\wbl-0.2.0
```

产物：

```text
publish\wbl-0.2.0\WBL.exe
```

唯一发行目标是 `WBL.exe`。打包脚本会把运行时依赖压缩后附加到 exe 尾部，启动时由程序内置的
AssemblyResolve 入口加载。图标源文件为 `src/WorldBirdLauncher.App/Assets/app-icon.svg`，
应用程序图标包含 16、24、32、48、64、128、256 像素尺寸。

## 项目结构

```text
WBL.sln
src/
  WorldBirdLauncher.App/       WPF 客户端
  WorldBirdLauncher.Core/      启动器核心逻辑
  WorldBirdLauncher.Shared/    平台无关模型、协议与兼容层
tests/
  WorldBirdLauncher.Core.Tests/
scripts/
  pack-net48.ps1
```

## 第三方组件

客户端包含 WebView2 与 `skinview3d`。相关许可信息随对应资源文件保留。

## 许可证

本项目使用 [MIT License](LICENSE)。
