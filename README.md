# quiet-windows-toolkit

Windows 本机「深夜静音」工具集：让风扇降速、机械硬盘整盘脱机，白天自动恢复。

这套东西解决的是一个很具体的痛点：夜里机器还开着，14TB / 4TB 机械盘和风扇的噪音很烦，
但又不希望白天起床后还得手动把盘联回来。于是拆成三层：

| 层 | 做什么 | 关键文件 |
|---|---|---|
| 风扇 | 通过 FanControl 的命名管道切换常态 / 静音配置 | `QuietMode/QuietMode.cs` |
| 硬盘 | 整盘 `Set-Disk -IsOffline`，零 I/O；由计划任务 23:58 脱机、10:02 联机 | `quiet-hdd-offline.ps1` |
| 系统 | 空闲停转 DISKIDLE、自动维护任务、开机补跑对账 | `quiet-hdd-*.ps1`、`diskgate` 闸门体 |

`QuietMode.exe` 是托盘程序：右键看当前状态（静音 / 风扇档 / 两块盘脱机与否），
一键开关静音、单独开关硬盘空闲停转、单独开关计划闸门、设置开机自启。

> 仓库内容已脱敏。所有本机账号名、用户目录、工作区路径、软件安装路径都替换成了
> `<USER>` / `<FAN_ROOT>` / `<TOOLS>` / `<WORKSPACES>` 等占位符，部署时按你的机器改。

## 目录

```
.gitattributes              统一 LF 换行
enable-utf8-console.ps1     PS 5.1 控制台 UTF-8 输出
set-winx-pwsh.ps1           PowerShell 7 终端配置辅助
run_ps.py                  中文路径 + 嵌套引号安全的 PowerShell 调用包装
ide-terminal-settings.jsonc VS Code / Cursor / Trae 终端配置示例
fix-rtss-scanlinesync.ps1   RTSS 扫描线同步修复
quiet-hdd-*.ps1             机械盘夜间闸门的安装 / 状态 / 暂停 / 磁盘号配置
QuietMode/
  QuietMode.cs              托盘程序完整源码（C# / .NET Framework 4.6）
  QuietMode.exe.config      运行时配置
  build.cmd                 一键编译
  build/QuietMode.csproj    SDK 风格工程文件
```

## 快速开始

### 1. 编译托盘程序

需要 .NET SDK（6.0 或更高）与 `Microsoft.NETFramework.ReferenceAssemblies` 包
（首次构建会自动还原，需要网络）。双击 `QuietMode/build.cmd`，产物写到
`QuietMode/QuietMode.exe`。

### 2. 改配置

`QuietMode.cs` 里 `Cfg` 的默认路径都是占位符，第一次运行后在托盘菜单「设置…」里
填你的实际路径即可，配置保存在 `%APPDATA%\QuietMode\settings.ini`：

- `<FAN_ROOT>` —— FanControl 安装目录与 `IpcCtl.exe` 所在目录
- `<TOOLS>` —— HWiNFO 等开机自启工具的目录（不要放在准备夜间脱机的盘上）
- `C:\ProgramData\QuietHDD\diskgate.conf` 里的 `Disks=` 填你的机械盘磁盘号

### 3. 安装夜间闸门

以管理员身份运行：

```powershell
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1"
```

它会注册两个计划任务（23:58 脱机 / 10:02 联机 / 开机补跑），生成
`C:\ProgramData\QuietHDD\diskgate.ps1` 与 `diskgate.conf`。
常用参数：`-Status` 查看、`-TestNight` 试跑夜间逻辑、`-Restore` 回滚。

改脱机磁盘号：

```powershell
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-setdisks.ps1" -Disks '0,1' -Force
```

今晚想用盘：

```powershell
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-pause.ps1" -Pause
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-pause.ps1" -Resume -ReArm
```

### 4. 行为说明

- 开机后 2 分钟的 AtStartup 闸门会按「开机时长 < 15 分钟则 06:00 起算白天」判定，
  早上开机直接联机；00:00–05:59 的无人值守重启仍保持夜间脱机。
- 静音模式与「硬盘空闲停转」是**两个独立开关**，互不绑定；停转分钟数可单独设置。
- 关闭静音模式会做完整恢复：风扇常速、两块盘联机、6 项维护任务启用、闸门 RESUMED。

## Windows 中文编码约定

本机 PowerShell 5.1 Desktop + zh-CN / 代码页 936(GBK)，未开系统级 UTF-8；Bash 侧 UTF-8 正常。

- 优先 Bash；必须用 PowerShell 时开头写
  `$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)`。
- 读文件用 `Get-Content -Raw -Encoding UTF8`；写文件用
  `[System.IO.File]::WriteAllText($p,$s,(New-Object System.Text.UTF8Encoding($false)))`。
- 含中文的 `.ps1` 存 UTF-8 带 BOM；`.bat/.cmd` 存 GBK(936) 无 BOM；Python 显式 `encoding="utf-8"`。
- 中文路径一律加引号并用 `-LiteralPath`；命令同时含中文路径与嵌套引号时走 `run_ps.py`。

## 环境要求

- Windows 10 / 11，PowerShell 5.1
- .NET Framework 4.6 运行时（托盘程序）+ .NET SDK（编译）
- 脱机 / 联机磁盘、启停计划任务需要管理员权限（托盘程序会按需弹 UAC）
- FanControl（风扇曲线切换，用户态命名管道，无需管理员）

## 许可

MIT，见 `LICENSE`。
