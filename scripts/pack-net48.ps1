<#
    阶段 3：把 net48 客户端打成"小体积单文件 exe"。

    net48 没有 .NET 8 那样的自包含单文件发布，所以这里自研：
      ① dotnet build -c Release -f net48
      ② 把输出目录里的托管依赖 + 原生 WebView2Loader.dll 压缩成一个 zip 载荷
      ③ 追加到 exe 尾部：[exe][zip][int64 长度][魔数 MCSLPAY1]
    运行时由 src\WorldBirdLauncher.App\Program.cs 的入口负责从载荷里解析程序集、并把原生 dll 解到
    %LOCALAPPDATA%\WorldBirdLauncher\native\<版本>。

    用法：pwsh -File scripts/pack-net48.ps1 [-Out <目录>] [-Lite]
    产物：<Out>\WBL-Pro.exe；加 -Lite 时是 <Out>\WBL.exe
#>

param(
    [string]$Out = '',
    [switch]$Lite
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\WorldBirdLauncher.App\WorldBirdLauncher.App.csproj'
$bin = Join-Path $repoRoot 'src\WorldBirdLauncher.App\bin\Release\net48'
if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $repoRoot 'publish\_staging-net48'
}

Write-Host '① 构建 net48 Release…' -ForegroundColor Cyan
# PATH 里不一定有 dotnet（本机就是），优先用常见安装位置
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
if (-not $dotnet -or -not (Test-Path $dotnet)) {
    $candidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) { throw '找不到 dotnet.exe，请把 .NET SDK 装好或加进 PATH' }

$buildArgs = @('build', $project, '-c', 'Release', '-f', 'net48', '-v', 'q', '--nologo')
if ($Lite) { $buildArgs += @('-p:LiteEdition=true') }
& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }

# 只嵌入运行时真正需要的东西：跳过 exe 自己、pdb、xml 文档、exe.config（绑定重定向由解析器代替）
$exeName = if ($Lite) { 'WBL.exe' } else { 'WBL-Pro.exe' }
$payloadFiles = Get-ChildItem $bin -File | Where-Object {
    # 两个发行版共用 bin 目录，另一种发行版的 exe 也不能塞进载荷
    $_.Extension -ne '.exe' -and
    $_.Extension -notin @('.pdb', '.xml') -and
    $_.Extension -ne '.config'
}
if (-not $payloadFiles) { throw "输出目录里没有可嵌入的依赖：$bin" }

Write-Host ('② 打包载荷（' + $payloadFiles.Count + ' 个文件）…') -ForegroundColor Cyan
$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) ('mcsl-payload-' + [Guid]::NewGuid().ToString('N') + '.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $bin, $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

$exeBytes = [System.IO.File]::ReadAllBytes((Join-Path $bin $exeName))
$zipBytes = [System.IO.File]::ReadAllBytes($zipPath)

$output = New-Object System.IO.MemoryStream
$output.Write($exeBytes, 0, $exeBytes.Length)
$output.Write($zipBytes, 0, $zipBytes.Length)
$lengthBytes = [System.BitConverter]::GetBytes([int64]$zipBytes.Length)
$output.Write($lengthBytes, 0, $lengthBytes.Length)
$magicBytes = [System.Text.Encoding]::ASCII.GetBytes('MCSLPAY1')
$output.Write($magicBytes, 0, $magicBytes.Length)

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$target = Join-Path $Out $exeName
[System.IO.File]::WriteAllBytes($target, $output.ToArray())
$output.Dispose()
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

$size = [math]::Round((Get-Item $target).Length / 1MB, 2)
Write-Host ("③ 单文件完成：$target（$size MB）") -ForegroundColor Green
