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
  Program.cs                托盘入口（单实例 / --diag / --status / --selftest）
  App/                      路径与日志、HKCU 自启、Win11 托盘常显
  Core/                     qmctl 调用通道 + 只读 JSON 解析（与引擎跨进程只走 JSON）
  Ui/                       托盘菜单 / 设置窗口 / 图标
  Diag/                     自检输出
  QuietMode.exe.config      运行时配置
  build.cmd                 一键编译
  build/QuietMode.csproj    SDK 风格工程文件
```

## 快速开始

### 1. 编译

需要 .NET SDK（6.0 或更高）与 `Microsoft.NETFramework.ReferenceAssemblies` 包
（首次构建会自动还原，需要网络）。

```
dotnet build QuietMode.sln -c Release     # Core / Windows / qmctl / 托盘 / 单测
QuietMode\build\build.cmd                # 只编托盘，产物复制到 QuietMode\QuietMode.exe
```

v2 的托盘只是 `qmctl` 的前端，**部署时必须把 `qmctl.exe` 放在 `QuietMode.exe` 同目录**
（或在 `%APPDATA%\QuietMode\tray.ini` 写 `QmctlPath=<完整路径>`）。
找不到 qmctl 时托盘会把所有操作标成不可用，不会退回旧的自带逻辑。
布局与契约见 [`docs/TRAY.md`](docs/TRAY.md)。

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
- .NET Framework 4.8.1 运行时（托盘 / qmctl）+ .NET SDK（编译）
- 脱机 / 联机磁盘、启停计划任务需要管理员权限（托盘程序会按需弹 UAC）
- FanControl（风扇曲线切换，用户态命名管道，无需管理员）

## v2.0 重构（进行中）

完整规格见 [`docs/V2_SPEC.md`](docs/V2_SPEC.md)。按阶段推进，不一次性重写全部代码。

- **Phase 0 代码审计与行为基线 — 已完成**
  - `docs/LEGACY_AUDIT.md`：全部脚本入口、计划任务、注册表 / 服务 / 电源 / 磁盘 / 进程 / FanControl IPC 修改点，依赖图，缺口清单；未识别的系统修改 = 0。
  - `docs/LEGACY_BEHAVIOR.md`：夜间 / 白天 / 开机补跑 / Pause-ReArm / DISKIDLE / 自动静音的实测行为基线，含 2026-11-10 夜间实测异常。
  - `docs/ARCHITECTURE.md`：现状架构、v2 目标映射、Phase 0–7 路线与迁移安全要点。
- **Phase 1 统一配置与快照 — 已完成**
  - 新增 `src/QuietMode.Core`（配置模型、校验、迁移、原子写、快照模型、维护任务唯一清单）与 `src/QuietMode.Windows`（WMI / 任务 COM / 注册表 / 电源只读探测 + `SnapshotManager`）。
  - `qmctl` 命令行：`config show|validate|init|migrate`、`snapshot create|verify|show`、`state show`、`disks list`。
  - `C:\ProgramData\QuietMode\{config.json, state.json, baseline.json}`；`config.backup.json` 为 last-known-good，损坏自动回退。
  - 21 项单测（`tests/QuietMode.Core.Tests`）+ `powershell\Test-QuietMode.ps1` 一键自检，全部通过。
  - 实机演练：v1 `settings.ini` 迁移为 v2，磁盘 #0/#1 由编号解析为 `uniqueId`+`serialNumber`（非编号匹配）；baseline 采集 10 项任务 / SysMain / MaintenanceBoundary / DISKIDLE 1200s / 5 块磁盘。
  - 详见 [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md)。本阶段所有代码路径均只读，不改系统状态。
- **Phase 2 磁盘管理重构 — 已完成**
  - `DiskIdentityResolver`：UniqueId 优先、序列号兜底、容量/总线复核；**磁盘编号永不参与匹配**，无身份或多命中一律拒绝执行。
  - `DiskSafetyGuard`：系统盘、启动盘、页面文件所在盘、存储池成员、状态未知 —— 硬阻断；`--force` 只能越过「占用」一项。
  - `Invoke-DiskOperation.ps1`（唯一写入口）在落盘前**用同一身份再复检一次**，执行 `Set-Disk -InputObject`，随后回读 `IsOffline` 验证；未提权返回 `ELEVATION_REQUIRED`，绝不假装成功。
  - 占用诊断：物理磁盘 I/O 计数器 + 进程路径/命令行线索（handle.exe 为可选增强，不是硬依赖）。
  - 新增 `ProcessRunner`（并行读 stdout/stderr、真超时、超时杀进程树），所有外部调用统一走它。
  - `qmctl disks resolve|inspect|offline|online|verify`，默认演练，`--apply` 才动手。
  - 单测 43 项 + `powershell\Test-DiskSafety.ps1` 只读集成测试（系统盘必须被拦住），全部通过。
  - 详见 [`docs/DISK_MANAGEMENT.md`](docs/DISK_MANAGEMENT.md)。
- **Phase 3 Windows 维护管理重构 — 已完成**
  - 唯一维护清单（`MaintenanceCatalog`）+ 禁改范围自检：Windows Update / Defender / Firewall / BitLocker 等路径写进清单也会被拦下。
  - 补齐 v1 漏掉的 `Data Integrity Scan for Crash Recovery`；`Regular Maintenance` 按 `NotPresent` 处理（本机确实不存在）。
  - `MaintenancePlanner` 把夜间顺序固化为步骤：StopTask → DisableTask → SysMain → MaintenanceBoundary / WakeUp；白天严格按 baseline 恢复，baseline 没记录的值一律不写。
  - `MaintenanceVerifier` 三态结论：`Ok` / `Degraded`（任务仍 Running、读取失败）/ `Failed`（仍未禁用、注册表没写进去）。
  - 每个写动作 `-ErrorAction Stop` + 写后复读；C# 再独立复观测一次才下结论。未提权 `--apply` 步骤标 `[未执行]`，退出码 2。
  - 单测 60 项 + `powershell\Test-MaintenanceSafety.ps1` 只读集成测试，全部通过。
  - 详见 [`docs/MAINTENANCE.md`](docs/MAINTENANCE.md)。
- **Phase 4 统一 Worker 与调度 — 已完成**
  - 只保留一个计划任务 `\QuietMode\QuietMode-Reconcile`：23:58 / 10:02 / 开机+2min / 睡眠恢复 / 解锁 / 登录，夜间每 10 分钟校验一次；SYSTEM + Highest、`WakeToRun=false`、忽略并发、10 分钟执行上限。
  - `StateMachine` 是唯一决策点（10 个状态，优先级：恢复 → 手动 → 暂停 → 时间 → 默认白天），`StateMachine.ApplyRun` 是唯一状态写回点。
  - `ScheduleWindow` 用完整 `HH:mm` 判跨午夜窗口，开机补偿显式配置（默认开机 15 分钟内、06:00 之后算白天）。
  - `ReconcilePlanner` 固定顺序：夜间 维护→磁盘→风扇→验证，白天 磁盘→维护→风扇→验证。
  - `Invoke-QuietModeWorker.ps1` 是唯一执行器：接收请求 JSON，只按步骤做事，不做判断；未提权时全部步骤标 `notrun`，绝不做一半。
  - 风扇改用 `IpcCtl.exe list` 的 `current=` 回读真实配置（v1 注释里「FanControl 不暴露当前配置名」不成立）。
  - `qmctl reconcile [auto|night|day|restore|verify] [--trigger X] [--apply]`、`qmctl task status|print|install|uninstall|disable-legacy`、`qmctl state pause|resume|rearm|manual-quiet|manual-normal|auto`。
  - 单测 76 项 + `powershell\Test-SchedulingSafety.ps1` 只读集成测试（任务 XML 唯一性、演练不改状态、未提权必失败），全部通过。
  - 详见 [`docs/SCHEDULING.md`](docs/SCHEDULING.md)。
- **Phase 5 C# 托盘重构 — 已完成**
  - 2424 行单文件拆成 `Program` + `App`（路径/日志/自启）+ `Core`（qmctl 契约 + 只读 JSON）+ `Ui`（托盘/设置/图标）+ `Diag`。
  - 删除 `Apply()` 与 `CheckAutoSilent()`：托盘不再自己算窗口、不再拼提权脚本、不再直接碰 WMI / 注册表 / 电源方案。
  - 新增 `qmctl status`（一次调用给出窗口/决策/风扇/磁盘/停转/任务的完整事实）、`qmctl apply <intent>`（一次用户动作 = 一次提权 = 一次 reconcile）、`qmctl config set`、`qmctl spin show|set`。
  - 新增 `SpinDownController`：DISKIDLE 独立开关，`powercfg` 写入后独立回读，不一致即 `VERIFY_MISMATCH`。
  - 修正 `Environment.TickCount & 0x7FFFFFFF` 在开机 24.9 天后回绕成“刚开机几分钟”、导致 06:00–10:02 夜间窗口被误判成白天的问题（改用 `GetTickCount64`）。
  - 界面只显示观测值：`offline`/`exists` 读不到时序列化为 `null`，显示“状态未知”而不是“正常”；计划任务未注册、v1 闸门仍启用都会常驻警告。
  - 单测 81 项（新增 5 项托盘契约）+ `Test-QuietMode.ps1` 新增“托盘契约：status 字段齐全（只读）”，全部通过。
  - 详见 [`docs/TRAY.md`](docs/TRAY.md)。
- **Phase 6–7**：安装迁移恢复、测试与 CI 收尾，见规格 §11。

```powershell
# 编译 + 自检（不改系统状态）
.\build\build.ps1
.\powershell\Test-QuietMode.ps1

# 查看当前配置 / 磁盘身份
.\bin\qmctl.exe config validate
.\bin\qmctl.exe disks list
```

审计结论：v1 存在两套并行闸门、维护任务清单在 6 处重复定义且不一致、白天恢复用硬编码默认值而非原始快照、磁盘身份只用磁盘编号、Worker 只报 `*_REQUESTED` 不回读验证等 10 项结构性缺陷。系统盘 / 页面文件保护与 DISKIDLE 独立性两项已满足，重构时不得回退。

## 许可

MIT，见 `LICENSE`。
