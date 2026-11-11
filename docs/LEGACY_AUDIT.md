# LEGACY AUDIT — v1 现状全量盘点（Phase 0）

> 审计对象：`quiet-windows-toolkit` v1（2026-11-11 01:xx 实机状态）
> 方法：源码静态盘点 + 本机运行时读取 + 计划任务定义 XML + 真实运行日志交叉验证
> 本文已脱敏：本机账号 / 工作区路径 / 软件安装路径 / 磁盘序列号均替换为占位符
> 完成标准：**不允许有未识别的系统修改行为** → 见第 14 节，未识别项 = 0

---

## 1. 组件清单

| 组件 | 位置 | 行数/大小 | 角色 |
|---|---|---|---|
| `QuietMode.cs` | 源码根 `QuietMode/` | 2424 行 | C# WinForms 托盘，单文件，含 Cfg / Sys / Diag / TrayApp / SettingsForm |
| `QuietMode.exe` | 构建产物（不入库） | 约 76 KB | 托盘可执行 |
| `QuietMode.exe.config` | 源码根 | 174 B | net46 运行时声明 |
| `build/QuietMode.csproj` | 源码根 | 1.2 KB | SDK 风格工程，`net46` |
| `build.cmd` | 源码根 | 520 B | 一键编译 + 替换 exe |
| `quiet-hdd-night.ps1` | 源码根 | ~10 KB | 旧版夜间策略入口（完整版） |
| `quiet-hdd-daytime.ps1` | 源码根 | ~12 KB | 旧版白天恢复 / 安装入口 |
| `quiet-hdd-offline.ps1` | 源码根 | ~22 KB | 设备安装器，内嵌 `diskgate.ps1` 生成体 |
| `quiet-hdd-pause.ps1` | 源码根 | ~7 KB | Pause / Resume / ReArm / Status |
| `quiet-hdd-fix.ps1` | 源码根 | ~16 KB | 计划任务注册修复（含 XML 回退路径） |
| `quiet-hdd-setdisks.ps1` | 源码根 | ~7.6 KB | 只改 `diskgate.conf` 的 `Disks=` / `Force=` |
| `quiet-hdd-status.ps1` | 源码根 | ~8 KB | 只读状态报告 |
| `diskgate.ps1` | `C:\ProgramData\QuietHDD\` | ~3.3 KB | **实际运行的磁盘闸门**（DiskGate 任务调用） |
| `gate.ps1` | `C:\ProgramData\QuietHDD\` | ~1.6 KB | **另一套仍在运行的维护闸门**（NightGate 任务调用） |
| `QuietHDD_NightGate.xml` | `C:\ProgramData\QuietHDD\` | 4.1 KB | 任务定义备份（UTF-16） |
| `diskgate.conf` | `C:\ProgramData\QuietHDD\` | 20 B | `Disks=0,1` / `Force=0` |
| `spindown.orig` | `C:\ProgramData\QuietHDD\` | 8 B | 唯一一处电源原始值快照，内容 `00000000` |
| 运行时脚本 | `<APPDATA>\QuietMode\` | 多个 | `worker.ps1` / `gate.ps1` / `spindown.ps1` / `fixgate.ps1` / `fixhwinfo.ps1` / `diag_*.ps1`，全部由 C# 动态生成 |

另有 `run_ps.py`、`enable-utf8-console.ps1`、`set-winx-pwsh.ps1`、`fix-rtss-scanlinesync.ps1`、`ide-terminal-settings.jsonc` 为通用编码/环境工具，**不参与夜间静音业务**。

---

## 2. 脚本入口与参数（全部已识别）

| 脚本 | 参数 | 主要副作用 |
|---|---|---|
| `quiet-hdd-night.ps1` | `-Restore` `-KeepDefrag` `-KeepSysMain` `-SpinDownHDD` `-SpinDownMinutes` `-MaintenanceHour`（默认 14） `-FromElevated` `-NoElevate` | 停/禁 7 项维护任务、禁 Regular Maintenance、停 SysMain 并设 Disabled、写 MaintenanceBoundary / WakeUp、powercfg DISKIDLE=0、`-Restore` 反向恢复 |
| `quiet-hdd-daytime.ps1` | `-Restore` `-NoElevate` `-FromElevated` | 注册 `QuietHDD_NightGate`（23:58 / 10:02 / AtStartup+2min）、启用 6 项维护任务、删 MaintenanceBoundary / WakeUp、SysMain 恢复 Automatic、写 MaintenanceBoundary=10 / WakeUp=0 |
| `quiet-hdd-offline.ps1` | `-Restore` `-Status` `-TestNight` `-NoImmediate` `-Force` `-DiskNumbers` `-SpinDownSeconds` `-NoElevate` `-FromElevated` | 生成 `diskgate.ps1` + `diskgate.conf`、注册 `QuietHDD_DiskGate`、启用/禁用维护闸门、powercfg 恢复 |
| `quiet-hdd-pause.ps1` | `-Pause` `-Resume` `-ReArm` `-Status` `-NoElevate` `-FromElevated` | Disable DiskGate 并强制联机 / 仅 Enable / Enable 后按当前时钟立刻跑一次闸门 |
| `quiet-hdd-fix.ps1` | `-NoElevate` `-FromElevated` | 重新注册 `QuietHDD_NightGate`，含 Register-ScheduledTask 对象路径与 XML 回退路径 |
| `quiet-hdd-setdisks.ps1` | `-Disks '0,1'` `-Force` `-NoElevate` `-FromElevated` | 只写 conf，含不调用 `Set-Disk` 的干跑预演 |
| `quiet-hdd-status.ps1` | `-NoElevate` `-FromElevated` | 只读；报告维护任务、MaintenanceBoundary / WakeUp、闸门任务、磁盘状态 |
| `QuietMode.exe` | `--diag` `--fixhwinfo` `--fixgate` | 自检 / 一次性改 `\HWiNFO` 任务路径 / 一次性给 `diskgate.ps1` 打开机补跑补丁 |

托盘菜单（12 项）：状态行 ×4、计划闸门开关、硬盘空闲停转开关、设置停转时长、开启/关闭静音、重新应用当前模式、立即联机硬盘、设置…、刷新状态、托盘图标被隐藏？、查看日志、退出。

---

## 3. 计划任务盘点

### 3.1 本工具自己的任务（两套闸门并存）

| 任务 | 动作 | 触发器 | 主体 | 其他 |
|---|---|---|---|---|
| `\QuietHDD\QuietHDD_NightGate` | `powershell.exe -File C:\ProgramData\QuietHDD\gate.ps1` | Daily 23:58 / Daily 10:02 / AtStartup `PT2M` | `S-1-5-18` (SYSTEM) | `RunLevel=HighestAvailable`，`WakeToRun=false`，`StartWhenAvailable` |
| `\QuietHDD\QuietHDD_DiskGate` | `powershell.exe -File C:\ProgramData\QuietHDD\diskgate.ps1` | Daily 23:58 / Daily 10:02 / AtStartup `PT2M` | SYSTEM | `RunLevel=Highest` |

证据：`QuietHDD_NightGate.xml` 的 `<StartBoundary>` / `<Delay>` / `<UserId>` / `<WakeToRun>`；`quiet-hdd-offline.ps1` 的 `Register-ScheduledTask` 段；`gate.log` 与 `diskgate.log` 在 11-10 23:58 各有独立记录，证明**两个任务当前都在运行**。

### 3.2 被管理的 Windows 维护任务

| TaskPath | TaskName | night.ps1 | daytime.ps1 | status.ps1 | fix.ps1 | gate.ps1 | C# Cfg |
|---|---|---|---|---|---|---|---|
| `\Microsoft\Windows\Defrag\` | ScheduledDefrag | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `\Microsoft\Windows\DiskCleanup\` | SilentCleanup | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `\Microsoft\Windows\Data Integrity Scan\` | Data Integrity Check And Scan | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `\Microsoft\Windows\Data Integrity Scan\` | Data Integrity Scan | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `\Microsoft\Windows\Data Integrity Scan\` | **Data Integrity Scan for Crash Recovery** | ✔ | ✘ | ✘ | ✘ | ✘ | ✘ |
| `\Microsoft\Windows\Chkdsk\` | ProactiveScan | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `\Microsoft\Windows\Shell\` | IndexerAutomaticMaintenance | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `\Microsoft\Windows\TaskScheduler\` | **Regular Maintenance** | ✔（仅夜间禁用） | ✘（无恢复） | ✘ | ✘ | ✘ | ✘ |

**这是 v1 最严重的结构缺陷之一：同一份业务清单在 6 处重复定义且互不一致。**
- Crash Recovery 任务只有 `night.ps1` 禁用，白天没有任何地方恢复。
- Regular Maintenance 只有 `night.ps1` 禁用，白天没有任何地方恢复。
- 托盘 Worker（`QuietMode.cs`）完全不管理这两项，也不管理 SysMain / 注册表。

### 3.3 其他任务

- `\HWiNFO` 登录任务：一次性维护命令 `--fixhwinfo` 把路径从旧盘迁到 `<TOOLS>`；不参与夜间业务。
- C# `AutoStart` 写 `HKCU\...\Run\QuietMode`，不是计划任务。

---

## 4. 注册表修改（全部已识别）

| 键 | 值 | 夜间 | 白天 | 位置 |
|---|---|---|---|---|
| `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance` | `MaintenanceBoundary` (DWord) | 写 `MaintenanceHour`（night 默认 14 / daytime 默认 10） | `Remove-ItemProperty` 直接删除 | night / daytime / status |
| 同上 | `WakeUp` (DWord) | 写 `0` | `Remove-ItemProperty` 直接删除 | night / daytime / status |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | `QuietMode` (String) | 自启开关写入程序路径 | — | `Cfg.SetAutoStart` |
| `HKCU\Control Panel\NotifyIconSettings\<hash>` | `IsPromoted` (DWord) | 写 `1` 令托盘图标常显 | — | `Sys.EnsureTrayPromoted` |
| `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\...` | `ACSettingIndex` / `DCSettingIndex` | 只读回显 | 只读回显 | `Sys.DiskIdleSeconds` |

**问题**：白天恢复用的是「删除值 + SysMain 硬设 Automatic」，不是原始快照恢复。若用户原本 `WakeUp=1`、原本 MaintenanceBoundary 存在、原本 SysMain=Disabled，v1 会把它们改成工具默认值，破坏用户配置。方案 §4.3 / §13.11 明确禁止。

---

## 5. 服务修改

| 服务 | 夜间 | 白天 | 位置 |
|---|---|---|---|
| `SysMain` | `Stop-Service` + `Set-Service -StartupType Disabled` | `Set-Service -StartupType Automatic` + `Start-Service` | `quiet-hdd-night.ps1` / `quiet-hdd-daytime.ps1` |

- 托盘 Worker **完全不碰 SysMain**（夜间由 NightGate 的 `gate.ps1` 也不碰，只有旧 `night.ps1` 碰）。
- `-KeepSysMain` 开关可跳过夜间禁用 —— 方案 §0.1 要求默认策略必须关闭它。
- 白天恢复为硬编码 `Automatic`，未记录原始 StartupType / Status。

---

## 6. 电源修改

| 设置 | 命令 | 位置 |
|---|---|---|
| DISKIDLE AC | `powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec` | `Sys.BuildSpinScript`（托盘独立开关）、`night.ps1`（夜间设 0）、`offline.ps1 -Restore` |
| DISKIDLE DC | `powercfg /setdcvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec` | `Sys.BuildSpinScript` |
| 应用 | `powercfg /s SCHEME_CURRENT` | 同上 |
| 原始值快照 | `C:\ProgramData\QuietHDD\spindown.orig`（8 B，`00000000`） | `offline.ps1` |

- 托盘的空闲停转已与静音模式解耦（独立开关、分钟数、AC/DC 同写、注册表回读校验），这一点符合方案 §8。
- 但**只有一处 8 字节快照**，没有按电源方案分别记录 AC/DC，也没有纳入统一 baseline。

---

## 7. 磁盘修改

| 项 | v1 实现 |
|---|---|
| 身份 | **磁盘编号字符串** `Disks=0,1`（`diskgate.conf`、`Cfg.HddDisks`）；无任何稳定身份字段 |
| 脱机 | `Set-Disk -Number $n -IsOffline $true -ErrorAction Stop` |
| 联机 | `Set-Disk -Number $n -IsOffline $false -ErrorAction Stop` |
| 保护 | `IsBoot` / `IsSystem`、含页面文件盘跳过、进程占用 `SKIP_BUSY`（可用 `Force=1` 覆盖） |
| 占用检测 | `Get-CimInstance Win32_Process` 的 `ExecutablePath` / `CommandLine` 匹配 `[DEJ]:\`，只作辅助 |
| 结果 | 日志文本 `OFFLINE:Disk0` / `ONLINE:Disk0:letters=J:` / `SKIP_BUSY:Disk1:pids=...` / `ALREADY_OFF` |
| 验证 | 脚本内**有** `Get-Disk` 回读；但托盘 Worker 只输出 `HDD=OFFLINE_REQUESTED` / `ONLINE_REQUESTED`，**不回读验证** |

实机磁盘身份（脱敏展示，字段可用）：

| 编号 | 型号 | BusType | IsBoot | IsSystem | 页面文件 | 用途 |
|---|---|---|---|---|---|---|
| 0 | `<HDD_MODEL_0>` 14 TB | SATA | False | False | 无 | 夜间目标盘 |
| 1 | `<HDD_MODEL_1>` 4 TB | SATA | False | False | 无 | 夜间目标盘 |
| 2 | `<NVME_SYSTEM>` | NVMe | **True** | **True** | `<VOL_SYS>\pagefile.sys` | 系统盘，绝不碰 |
| 3 / 4 | `<NVME_OTHER>` | NVMe | False | False | 无 | 不参与 |

`MSFT_Disk` 的 `UniqueId` / `SerialNumber` / `FriendlyName` / `Size` / `BusType` 均可读取，v2 的稳定身份识别有数据基础。`IsBoot`/`IsSystem` 与页面文件盘保护当前有效。

---

## 8. FanControl IPC

| 项 | 值 |
|---|---|
| 客户端 | `<FAN_ROOT>\ipc\IpcCtl.exe` |
| 命令 | `IpcCtl.exe load "<profile>"`，`IpcCtl.exe list` 首行 `current=<file> folder=...` |
| 常态配置 | `<FAN_ROOT>\Configurations\FanControl.json` |
| 静音配置 | `<FAN_ROOT>\Configurations\FanControl_Silent_Lite.json` |
| 标记文件 | `<FAN_ROOT>\silent_active.flag` |
| 运行进程 | `<FAN_ROOT>\FanControl.exe` |

FanControl 负责温度曲线与硬件控制，QuietMode 只切配置 —— 符合方案 §7.5 的前提。v1 有 `load` 后回读 `current=` 校验，但失败时只显示「FanControl 未响应」，未区分「IPC 不可用 / 配置文件不存在 / 切换后不匹配」。

---

## 9. 进程操作

| 位置 | 行为 | 风险 |
|---|---|---|
| `C:\ProgramData\QuietHDD\gate.ps1` | `Get-Process defrag -ErrorAction SilentlyContinue \| Stop-Process -Force` | **无条件杀掉所有名为 defrag 的进程**，方案 §4.2 明确禁止 |
| `diskgate.ps1` | `SKIP_BUSY` 时只记录 PID，不强杀 | 安全，但仅靠命令行正则，非完整句柄检测 |

`gate.log` 每次夜间都出现 `KILL:defrag`，证明该行为当前仍在生效。

---

## 10. 文件与配置落盘清单

```
C:\ProgramData\QuietHDD\
    QuietHDD_NightGate.xml     任务定义备份
    gate.ps1                   NightGate 实际脚本
    gate.log                   NightGate 运行日志
    diskgate.ps1              DiskGate 实际脚本（含开机补跑判定）
    diskgate.ps1.bak-*        历史备份
    diskgate.log              DiskGate 运行日志
    diskgate.conf             Disks=0,1 / Force=0
    install.log               安装日志
    spindown.orig             电源原始值（8 B）

<APPDATA>\QuietMode\
    settings.ini              策略配置（INI，无 schema / 无校验 / 非原子写）
    state.ini                 运行状态（INI）
    QuietMode.log             文本日志
    worker.ps1                C# 动态生成的提权 Worker
    worker_result.txt         纯文本结果（key=value 行）
    gate.ps1 / spindown.ps1 / fixgate.ps1 / fixhwinfo.ps1   一次性提权脚本
    diag.txt / diag_*.ps1 / diag_syntax.txt                 自检产物
    tip.done / activate.signal                             交互标记
```

配置文件特征：INI、UTF-8 无 BOM、`File.WriteAllText` 直接覆盖；无 schemaVersion、无类型校验、无 last-known-good、无原子替换、无 ACL 加固（`<APPDATA>` 用户可随意改）。

---

## 11. 功能依赖图（现状）

```
                       ┌──────────────────────────────┐
                       │  计划任务（两套并行，无仲裁）  │
                       │  \QuietHDD\NightGate  23:58   │
                       │  \QuietHDD\DiskGate   10:02   │
                       │  + AtStartup PT2M             │
                       └──────┬───────────────┬────────┘
                              │               │
                    gate.ps1  │               │  diskgate.ps1
              （6 维护任务、   │               │ （整盘脱机/联机、
                KILL:defrag） │               │  开机补跑、SKIP_BUSY）
                              ▼               ▼
  用户 ──► QuietMode.exe（托盘）──► C# BuildWorker ──► worker.ps1（提权）
   │            │                                      │
   │            │  Cfg.Load/Save                        ├─► diskgate.ps1 -ForceNight/-ForceDay
   │            ▼                                      ├─► 6 项维护任务 Enable/Disable
   │      settings.ini / state.ini                     ├─► \QuietHDD\ 两任务 Enable/Disable
   │                                                   └─► worker_result.txt（纯文本）
   │
   └──► 旧脚本（各自独立业务实现，互不共享清单）
         night.ps1 / daytime.ps1 / offline.ps1 / pause.ps1 / fix.ps1 / setdisks.ps1 / status.ps1

  旁路：AutoSilent（托盘定时器，只切风扇，不碰硬盘/维护/SysMain）
  旁路：BootReconcile（开机按实际磁盘状态做完整 Apply(false)）
  旁路：Sys.ApplyDiskIdle（独立 DISKIDLE 提权脚本）
```

三条控制路径（NightGate / DiskGate / 托盘 Worker）+ 七个旧脚本入口，彼此没有共享的配置、清单、快照或状态机。

---

## 12. 对照 v2 约束的缺口清单

| 方案条款 | 现状 | 结论 |
|---|---|---|
| §2 统一 JSON 配置 + 原子写 + last-known-good | INI 直接覆盖，无校验 | 缺 |
| §3 磁盘稳定身份（UniqueId/Serial） | 仅磁盘编号 `0,1` | 缺 |
| §3 结构化 DiskOperationResult + 实状态回读 | 日志文本；托盘只报 `*_REQUESTED` | 缺 |
| §4 唯一维护清单 + Regular Maintenance + Crash Recovery | 6 处重复定义且不一致 | 严重缺 |
| §4 禁用后重新查询验证 | `gate.ps1` / Worker 不回读 | 缺 |
| §4 SysMain 夜间禁用、白天恢复原始 | 仅旧脚本；恢复硬编码 Automatic | 部分缺 |
| §4 MaintenanceBoundary / WakeUp 快照恢复 | 直接删值 / 硬写 10 与 0 | 缺 |
| §5 baseline.json 全量快照 | 只有 8 B `spindown.orig` | 缺 |
| §6 单一 Reconcile 调度 + 状态机 | 两个闸门 + 定时器 + 分散状态字段 | 缺 |
| §6 睡眠恢复 / 用户解锁 / 夜间周期校验 | 无 | 缺 |
| §7 ProcessRunner 并行异步读取 | `ReadToEnd` + `WaitForExit` 顺序 | 缺 |
| §7 静态 Worker + 参数文件 | C# 动态拼接 PowerShell 代码 | 缺 |
| §7 静音成功需验证全部组件 | 只验证风扇 + 磁盘 | 缺 |
| §8 DISKIDLE 独立 | 已解耦 | **已满足** |
| §9 安装/卸载/迁移/兼容包装器 | 7 个脚本各自为政 | 缺 |
| §10 Pester / C# 测试 / CI | 无 | 缺 |
| 安全：系统盘/页面文件保护 | 已有且有效 | **已满足** |
| 安全：不无条件杀 defrag | `gate.ps1` 违反 | **违规** |

---

## 13. 实测异常（2026-11-10 夜间，作为基线证据）

```
[23:58:03] diskgate.log  mode=NIGHT OFFLINE:Disk0 SKIP_BUSY:Disk1:pids=<8 个 PID>
[23:58:04] gate.log      mode=NIGHT OFF:6项 ... KILL:defrag
[23:58:36] QuietMode.log AutoSilent: want=silent -> FanControl_Silent_Lite.json OK
[23:58:48] QuietMode.log === StopAutoSilent (user) === -> 风扇回常态
[23:58:58] QuietMode.log === Apply OFF ===
[23:59:00] diskgate.log  mode=DAY ONLINE:Disk0 ... OK_ONLINE:Disk1
[23:59:01] worker_result HDD=ONLINE_REQUESTED / MT=ON:6项 / GATE=RESUMED:两个
```

即：**夜间开始不到 1 分钟，一次用户「退出自动静音」连带触发了完整白天恢复，把刚建立的夜间策略全部撤销，且两块盘重新联机。** 这同时暴露三个问题：夜间策略无仲裁、状态机无优先级、`*_REQUESTED` 不等于实际状态验证。

另一条：`SKIP_BUSY:Disk1` 说明进程占用检查只靠命令行正则，遇到多进程占用时整块盘跳过，夜间静音不完整，且没有自动重试。

---

## 14. 未识别项

- 已逐项覆盖：脚本入口、计划任务、注册表、服务、电源、磁盘、进程、FanControl IPC、文件落盘、配置、日志、菜单、命令行开关、状态字段。
- 唯一未能直接读取的是 `\QuietHDD\` 两个任务的**实时 `State` 字段**（非管理员会话受任务文件夹 ACL 限制），但已通过任务定义 XML、`Register-ScheduledTask` 源码、以及 11-10 23:58 两份独立日志三方交叉验证其存在与运行，不构成未识别行为。
- 结论：**未识别的系统修改行为 = 0**。Phase 0 完成标准达成。

---

## 15. Phase 0 交付与下一阶段

产物：`docs/LEGACY_AUDIT.md`、`docs/LEGACY_BEHAVIOR.md`、`docs/ARCHITECTURE.md`、`docs/V2_SPEC.md`。

下一阶段为 **Phase 1（统一配置与快照系统）**：建立 `C:\ProgramData\QuietMode\` 目录与 ACL、`config.json` / `state.json` / `baseline.json` / `config.backup.json`、`ConfigValidator` / `ConfigMigrator` / `SnapshotManager`、原子写入、以及从 `settings.ini` + `diskgate.conf` + `spindown.orig` 的迁移读取。Phase 1 不改动任何运行时系统状态。
