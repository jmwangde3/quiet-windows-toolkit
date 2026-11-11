# QuietMode v2.0 完整重构实施方案（规格原文）

> 本文是项目维护者提供的 v2.0 方案原文，作为本仓库重构的唯一规格来源。
> 除把仓库归属账号替换成 `<OWNER>` 占位符外未作删改。
> Phase 0 审计结论见 `LEGACY_AUDIT.md`、`LEGACY_BEHAVIOR.md`、`ARCHITECTURE.md`。

---

## 0. 项目目标与不可变更要求

项目：`<OWNER>/quiet-windows-toolkit`（公开仓库）

目标：在保留现有功能和使用习惯的前提下，将项目重构为可靠、可恢复、可维护的 Windows 夜间静音管理工具。

### 0.1 核心产品要求

夜间静音是项目的核心功能，不允许在重构过程中弱化。

默认静音窗口：

- 开始：23:58
- 结束：10:02
- Windows 自动维护时间：10:00
- 禁止维护唤醒：WakeUp = 0

夜间必须执行：

1. 停止正在运行的指定磁盘维护任务。
2. 禁用指定 Windows 维护计划任务。
3. 禁用 Regular Maintenance 协调任务（存在时）。
4. 关闭 SysMain（默认策略）。
5. 将 Windows 自动维护时间调整到白天。
6. 禁止自动维护唤醒计算机。
7. 对配置的机械硬盘执行整盘脱机。
8. 将 FanControl 切换到指定静音配置。
9. 验证所有组件的实际状态。
10. 失败时自动重试，并在状态中明确显示异常。

白天必须：

1. 先联机目标机械硬盘。
2. 验证磁盘已经 Online。
3. 恢复 Windows 维护任务的原始启用状态。
4. 恢复 Regular Maintenance 的原始状态。
5. 恢复 SysMain 原始启动类型和运行状态。
6. 恢复自动维护注册表设置。
7. 恢复需要恢复的电源设置。
8. FanControl 切换到常态配置。
9. 验证全部组件的实际状态。

注意：

- 不允许把夜间禁用维护任务改成仅调整维护时间。
- 不允许删除夜间维护闸门。
- 不允许因为重构而默认开启 SysMain。
- 不允许取消机械硬盘整盘脱机功能。
- 不允许将 DISKIDLE 空闲停转与整盘脱机合并成同一个开关。
- 不允许在状态未知时显示静音成功。

### 0.2 安全优先级

优先级从高到低：

1. 防止误操作系统盘和启动盘。
2. 防止数据损坏。
3. 保证恢复能力。
4. 保证夜间静音策略。
5. 保证托盘 UI 和用户体验。

静音策略严格，但不能通过不安全的强制操作实现。

---

# 1. 总体架构

采用模块化架构，但不引入不必要的 Windows Service、数据库或网络服务。

保留：

- C# WinForms 托盘程序。
- PowerShell 5.1 系统管理能力。
- FanControl 命名管道 IPC。
- Windows Task Scheduler。
- Windows Storage PowerShell 模块。

建立唯一的业务控制层。

## 1.1 推荐目录结构

```text
quiet-windows-toolkit/
│
├── src/
│   ├── QuietMode.App/
│   │   ├── Program.cs
│   │   ├── TrayApp.cs
│   │   ├── MainMenu.cs
│   │   ├── SettingsForm.cs
│   │   ├── StatusView.cs
│   │   └── QuietMode.App.csproj
│   │
│   ├── QuietMode.Core/
│   │   ├── Models/
│   │   │   ├── QuietModeConfig.cs
│   │   │   ├── QuietModeState.cs
│   │   │   ├── DiskIdentity.cs
│   │   │   ├── MaintenanceTask.cs
│   │   │   └── OperationResult.cs
│   │   │
│   │   ├── Services/
│   │   │   ├── QuietModeController.cs
│   │   │   ├── ScheduleEngine.cs
│   │   │   ├── StateMachine.cs
│   │   │   ├── RecoveryManager.cs
│   │   │   └── DiagnosticService.cs
│   │   │
│   │   ├── Configuration/
│   │   │   ├── ConfigStore.cs
│   │   │   ├── ConfigValidator.cs
│   │   │   └── ConfigMigrator.cs
│   │   │
│   │   └── QuietMode.Core.csproj
│   │
│   └── QuietMode.Windows/
│       ├── DiskManager.cs
│       ├── DiskIdentityResolver.cs
│       ├── MaintenanceManager.cs
│       ├── ScheduledTaskManager.cs
│       ├── ServiceManager.cs
│       ├── PowerPlanManager.cs
│       ├── FanControlClient.cs
│       ├── ProcessRunner.cs
│       ├── SnapshotManager.cs
│       ├── FileHandleInspector.cs
│       └── QuietMode.Windows.csproj
│
├── powershell/
│   ├── Modules/
│   │   ├── QuietMode.Common.psm1
│   │   ├── QuietMode.Config.psm1
│   │   ├── QuietMode.Disk.psm1
│   │   ├── QuietMode.Maintenance.psm1
│   │   ├── QuietMode.Tasks.psm1
│   │   ├── QuietMode.Power.psm1
│   │   ├── QuietMode.Recovery.psm1
│   │   └── QuietMode.Diagnostics.psm1
│   │
│   ├── Invoke-QuietModeWorker.ps1
│   ├── Install-QuietMode.ps1
│   ├── Uninstall-QuietMode.ps1
│   ├── Repair-QuietMode.ps1
│   ├── Get-QuietModeStatus.ps1
│   └── Test-QuietMode.ps1
│
├── config/
│   └── default-policy.json
│
├── tests/
│   ├── QuietMode.Core.Tests/
│   ├── QuietMode.Windows.Tests/
│   ├── powershell/
│   │   ├── Config.Tests.ps1
│   │   ├── Disk.Tests.ps1
│   │   ├── Maintenance.Tests.ps1
│   │   ├── Recovery.Tests.ps1
│   │   └── Schedule.Tests.ps1
│   └── integration/
│       ├── scenarios.md
│       └── test-vm.md
│
├── docs/
│   ├── ARCHITECTURE.md
│   ├── SAFETY.md
│   ├── RECOVERY.md
│   ├── CONFIGURATION.md
│   ├── MIGRATION.md
│   ├── TESTING.md
│   └── TROUBLESHOOTING.md
│
├── .github/
│   └── workflows/
│       └── windows-ci.yml
│
├── README.md
├── CHANGELOG.md
├── LICENSE
└── QuietMode.sln
```

原则：

- C# 负责 UI、业务状态和调度决策。
- Windows 适配层负责具体系统操作。
- PowerShell 负责兼容 Windows 原生管理接口。
- 所有模块共享一份配置。
- 所有修改共享一份原始状态快照。
- 不允许各个脚本自行定义一套磁盘列表或维护任务列表。

不要求一次性将所有 PowerShell 代码改写成 C#。

优先统一接口和行为，再逐步迁移实现。

## 1.2 .NET 目标框架

当前项目使用 .NET Framework 4.6，建议升级到：

`net481`

理由：

- 保持 Windows 10 22H2 和 Windows 11 的兼容目标。
- 不必为了托盘程序引入额外的现代 .NET Runtime。
- WinForms、WMI、注册表和现有 Windows API 兼容性较好。
- 能降低一次性迁移风险。

.NET 10 是当前 LTS，但现代 .NET 10 对 Windows 10 的支持范围有限。如果以后确定只支持 Windows 11，可以再单独规划迁移到 `net10.0-windows`。

---

# 2. 配置系统重构

## 2.1 统一配置文件

机器级配置统一放到：

```text
C:\ProgramData\QuietMode\
    config.json
    state.json
    baseline.json
    config.backup.json
    logs\
    recovery\
```

用户 UI 偏好单独保存：

```text
%APPDATA%\QuietMode\
    ui-settings.json
```

职责：

| 文件 | 职责 |
|---|---|
| config.json | 当前机器策略和硬件配置 |
| state.json | 当前运行状态、手动覆盖和调度状态 |
| baseline.json | 安装前系统原始配置快照 |
| config.backup.json | 上一次有效配置 |
| logs | 结构化运行日志 |
| recovery | 故障恢复所需文件 |

机器配置目录必须设置合理 ACL，避免普通用户随意修改 SYSTEM 运行的策略或脚本。

## 2.2 配置格式

采用 JSON，增加：

```json
{
  "schemaVersion": 2,
  "schedule": {
    "enabled": true,
    "nightStart": "23:58",
    "dayStart": "10:02",
    "startupOverrideEnabled": true,
    "startupDayStart": "06:00",
    "startupGraceMinutes": 15,
    "reconcileIntervalMinutes": 10
  },
  "fan": {
    "enabled": true,
    "ipcPath": "",
    "normalProfile": "",
    "silentProfile": ""
  },
  "disks": {
    "enabled": true,
    "forceBusy": false,
    "targets": []
  },
  "maintenance": {
    "enabled": true,
    "strictNightDisable": true,
    "disableSysMainAtNight": true,
    "maintenanceHour": 10,
    "disableWake": true
  },
  "spinDown": {
    "enabled": false,
    "minutes": 20
  }
}
```

实际磁盘配置不再保存 Disk Number 作为主身份。

每块硬盘至少保存：

```json
{
  "uniqueId": "",
  "serialNumber": "",
  "friendlyName": "",
  "sizeBytes": 0,
  "busType": "",
  "enabled": true
}
```

磁盘编号仅作为当前运行时信息。

## 2.3 配置验证

必须实现：

- Schema 版本检查。
- 配置字段类型检查。
- 时间格式检查。
- 磁盘身份检查。
- 重复磁盘检查。
- 路径有效性检查。
- 风扇配置文件存在性检查。
- 不允许未知磁盘配置自动回退到 Disk 0。
- 配置损坏时加载 last-known-good 配置。
- 无有效配置时禁止任何不确定的磁盘操作。

所有配置写入使用：

1. 写临时文件。
2. Flush。
3. 验证 JSON。
4. 原子替换。
5. 保留上一个有效版本。

---

# 3. 磁盘管理重构

这是最高优先级模块之一。

## 3.1 稳定身份识别

使用以下字段：

- UniqueId：首选。
- SerialNumber：辅助验证。
- FriendlyName：型号校验。
- Size：容量校验。
- BusType：设备类型校验。

磁盘号不再用于长期保存。

执行时：

1. 获取当前全部磁盘。
2. 根据 UniqueId 匹配。
3. 验证 SerialNumber。
4. 验证型号和容量。
5. 确认唯一匹配。
6. 获取当前磁盘对象。
7. 重新执行安全检查。
8. 执行脱机或联机。
9. 再次查询并验证实际状态。

不要仅凭旧 Disk Number 执行操作。

可以通过已验证的磁盘对象执行：

```powershell
Set-Disk -InputObject $disk -IsOffline $true
```

完成后重新查询磁盘对象，验证 IsOffline。

## 3.2 磁盘安全保护

绝对禁止脱机：

- IsBoot = True。
- IsSystem = True。
- 包含活动页面文件。
- 包含系统启动所需分区。
- 无法唯一确认身份的磁盘。
- 存储池或其他不支持的特殊磁盘。
- 无法确定安全状态的磁盘。

出现异常：

- 记录具体原因。
- 不尝试换用另一个磁盘号。
- 不自动操作相似型号的硬盘。
- 不报告操作成功。

Force 只能处理明确允许用户覆盖的普通占用检查，永远不能绕过系统盘和启动盘保护。

## 3.3 文件和进程占用检查

将当前的进程路径正则检查降级为辅助诊断，不再把它当成完整的占用检测。

建议组合：

1. 磁盘级 I/O 计数。
2. 卷级活动信息。
3. 进程可执行路径。
4. 进程命令行。
5. 可选 Handle.exe 文件句柄检查。

Handle.exe 是可选依赖，不应成为整个程序运行的硬依赖。

对于无法识别的占用：

- 默认不强杀进程。
- 记录 PID、进程名称和占用路径。
- 显示磁盘脱机可能影响的程序。
- 允许高级用户明确选择 Force。
- 即使使用 Force，也不能绕过系统保护检查。

## 3.4 磁盘操作结果

统一返回结构化结果：

```text
DiskOperationResult
    DiskIdentity
    CurrentDiskNumber
    PreviousState
    RequestedState
    ActualState
    Success
    ErrorCode
    ErrorMessage
    Timestamp
```

不允许只输出：

```text
OFFLINE_REQUESTED
```

而没有确认磁盘真的 Offline。

---

# 4. Windows 夜间维护管理

这是项目的核心功能，必须完整保留。

## 4.1 建立唯一维护任务清单

所有维护任务只能在一处定义。

建议默认维护清单：

| TaskPath | TaskName |
|---|---|
| `\Microsoft\Windows\Defrag\` | ScheduledDefrag |
| `\Microsoft\Windows\DiskCleanup\` | SilentCleanup |
| `\Microsoft\Windows\Data Integrity Scan\` | Data Integrity Check And Scan |
| `\Microsoft\Windows\Data Integrity Scan\` | Data Integrity Scan |
| `\Microsoft\Windows\Data Integrity Scan\` | Data Integrity Scan for Crash Recovery |
| `\Microsoft\Windows\Chkdsk\` | ProactiveScan |
| `\Microsoft\Windows\Shell\` | IndexerAutomaticMaintenance |
| `\Microsoft\Windows\TaskScheduler\` | Regular Maintenance |

其中 Regular Maintenance 如果在当前 Windows 版本存在，也必须纳入夜间管理和白天恢复。

不存在的任务标记为 NotPresent，不要误报成功，也不要把不存在当成执行失败。

任务清单可以根据 Windows 版本扩展，但不能随意禁用整个 Microsoft\Windows 目录下的所有计划任务。

## 4.2 夜间执行流程

必须按以下顺序执行：

```text
EnterNight
    |
    v
保存或确认 baseline
    |
    v
停止指定维护任务
    |
    v
禁用指定维护任务
    |
    v
重新查询任务状态
    |
    v
验证全部现存目标任务 Disabled
    |
    v
关闭 SysMain
    |
    v
修改 MaintenanceBoundary
    |
    v
设置 WakeUp = 0
    |
    v
检查目标磁盘安全状态
    |
    v
目标磁盘 Offline
    |
    v
切换 FanControl 静音配置
    |
    v
全面验证
    |
    v
NightActive / NightDegraded
```

特别要求：

- 禁用任务必须使用 `-ErrorAction Stop`。
- 操作完成后必须重新读取任务。
- 不能仅凭 Disable-ScheduledTask 没有抛出异常就报告成功。
- 停止失败必须记录。
- 禁用失败必须记录。
- 目标任务仍然 Running 时不能报告维护闸门完全生效。
- 不要无条件结束所有名为 defrag.exe 的进程。
- 只允许终止能够确认属于目标维护任务的进程。

如果任务禁用失败，应进入 NightDegraded 并自动重试。

不要强行脱机正在执行关键维护操作且无法安全停止的磁盘。

## 4.3 白天恢复流程

顺序：

```text
EnterDay
    |
    v
目标磁盘 Online
    |
    v
验证全部目标磁盘 Online
    |
    v
恢复原始计划任务启用状态
    |
    v
恢复 Regular Maintenance
    |
    v
恢复 SysMain 原始状态
    |
    v
恢复注册表原始值
    |
    v
恢复电源设置
    |
    v
FanControl 切换常态配置
    |
    v
全面验证
    |
    v
DayActive / RecoveryPending
```

重要原则：

恢复的是安装前快照，而不是一套硬编码的 Windows 默认值。

例如：

- 原来启用的任务，白天恢复启用。
- 原来禁用的任务，白天仍然禁用。
- 原来 SysMain 是 Disabled，白天仍然 Disabled。
- 原来 SysMain 是 Manual，白天恢复 Manual。
- 原来注册表值不存在，恢复后仍然不存在。
- 原来 WakeUp 是 1，恢复为 1，而不是永远改成 0。

这样不会破坏用户原本的 Windows 配置。

## 4.4 自动维护注册表

对以下值保存：

```text
HKLM\SOFTWARE\Microsoft\Windows NT\
CurrentVersion\Schedule\Maintenance

MaintenanceBoundary
WakeUp
```

必须保存：

- 是否存在。
- 注册表类型。
- 原始数据。

恢复时按原始存在状态和数据恢复。

默认夜间策略：

- MaintenanceBoundary = 10。
- WakeUp = 0。

## 4.5 SysMain

默认策略：

- 夜间停止服务。
- 夜间设置 StartupType = Disabled。
- 白天恢复原始 StartupType。
- 白天恢复原始运行状态。

保留 KeepSysMain 高级配置，但默认必须关闭。

## 4.6 禁止扩大禁用范围

除已确认的维护任务外，不要默认禁用：

- Windows Update。
- Microsoft Defender。
- Windows Firewall。
- 核心系统服务。
- 其他与磁盘静音目标无直接关系的系统任务。

需要新增维护任务时，先检查实际磁盘访问行为，再更新清单。

---

# 5. 原始状态快照和恢复系统

这是 v2.0 必须重点建设的能力。

## 5.1 baseline.json

第一次安装时生成。

至少保存：

### Scheduled Tasks

- TaskPath。
- TaskName。
- 是否存在。
- 原始 Enabled 状态。
- 原始任务 XML 或必要的任务定义备份。
- 所属策略模块。

### Windows Services

- ServiceName。
- StartupType。
- Status。

至少包括 SysMain。

### Registry

- KeyPath。
- ValueName。
- 是否存在。
- ValueKind。
- 原始 Value。

### Power Plan

- ActivePowerScheme GUID。
- DISKIDLE AC。
- DISKIDLE DC。
- 被工具修改的其他电源设置。

### Disk

- UniqueId。
- SerialNumber。
- FriendlyName。
- Size。
- 原始 Online / Offline 状态。

### FanControl

- 当前配置文件。
- 工具切换前的配置状态。

### Snapshot Metadata

- SchemaVersion。
- Windows Build。
- 创建时间。
- 工具版本。
- 快照完整性校验值。

## 5.2 快照管理原则

- 第一次安装时创建 baseline。
- 日常切换静音不能覆盖 baseline。
- 升级版本不能自动覆盖 baseline。
- 用户主动重置原始状态时才允许重新建立 baseline。
- 每次修改前记录操作事件。
- 快照损坏时保留备份。
- 恢复操作失败时不能删除快照。

## 5.3 恢复事务

实现：

```text
RestoreAll
RestoreDisks
RestoreMaintenance
RestoreServices
RestorePower
RestoreFan
```

支持：

- 重复执行。
- 中途失败后再次执行。
- 部分恢复。
- 恢复进度记录。
- 恢复结果验证。

只有所有恢复项目都验证成功，才允许清理对应的安装文件和任务。

任何失败都保留 recovery 和 baseline。

不要递归删除同时存放其他模块数据的公共目录。

---

# 6. 调度和状态机

## 6.1 统一调度

删除多个互相竞争的调度实现。

统一使用一个受管理的任务：

```text
\QuietMode\QuietMode-Reconcile
```

任务触发：

1. 每天 23:58。
2. 每天 10:02。
3. 系统启动后延迟 2 分钟。
4. 系统从睡眠恢复时。
5. 用户解锁时。
6. 夜间定期校验，默认每 10 分钟。

定期校验只在计算机运行时执行，不允许唤醒计算机。

设置：

- RunLevel Highest。
- SYSTEM。
- WakeToRun = false。
- 防止重复并发执行。
- 设置合理的执行超时。
- 记录每次执行结果。

如果后续验证发现周期任务对目标设备有不必要的影响，可以调整周期，但不能取消启动和恢复后的补偿。

## 6.2 时间处理

使用完整的 HH:mm，而不是只使用 Get-Date.Hour。

默认：

```text
23:58 <= NIGHT < 10:02
```

必须正确处理跨午夜窗口。

保留当前项目的开机补偿规则：

- 开机时间小于 15 分钟。
- 当前时间处于 06:00–10:01。
- 按现有文档将早晨启动视为白天。
- 00:00–05:59 的无人值守重启保持夜间。

把这项行为改成显式配置，不能在重构时悄悄改变。

同时确保时间边界在 C#、PowerShell 和计划任务中完全一致。

## 6.3 状态机

定义：

```text
DayActive
EnteringNight
NightActive
NightDegraded
EnteringDay
ManualQuiet
ManualNormal
SchedulePaused
RecoveryPending
Error
```

状态转换必须集中管理。

不允许各模块独立修改 ModeOn、SchedulePaused 等状态而不通知总控制器。

## 6.4 状态优先级

建议：

1. 安全恢复状态。
2. 用户明确的手动覆盖。
3. 用户暂停调度。
4. 自动时间调度。
5. 默认日间状态。

用户手动开启静音后，不应被日间自动逻辑意外关闭。

用户手动关闭静音后，保留当前的今晚抑制行为，避免自动任务立即重新开启。

保留现有：

- Pause。
- Resume。
- Resume -ReArm。
- ScheduleMode override。
- ScheduleMode coexist。

但将它们统一映射到状态机。

---

# 7. C# 托盘程序重构

## 7.1 模块职责

将当前 2272 行 QuietMode.cs 拆分。

不要改变用户已经习惯的基本操作方式。

托盘保留：

- 双击切换静音。
- 右键菜单。
- 查看风扇状态。
- 查看目标硬盘状态。
- 查看维护闸门状态。
- 开启/关闭静音。
- 暂停/恢复计划。
- 单独设置硬盘空闲停转。
- 修改风扇配置路径。
- 修改目标磁盘。
- 开机自启。
- 自动静音时间设置。
- 诊断和修复。

## 7.2 ProcessRunner 修复

修复当前：

```csharp
ReadToEnd()
WaitForExit(timeout)
```

的顺序问题。

要求：

- stdout 和 stderr 并行异步读取。
- 使用真正的超时等待。
- 超时后安全终止子进程。
- 获取 ExitCode。
- 区分启动失败、UAC 取消、超时和执行失败。
- 保留输出。
- 不允许一个外部命令阻塞托盘 UI。

所有外部程序调用统一经过 ProcessRunner。

## 7.3 PowerShell Worker

不再由 C# 到处拼接和生成不同版本的 PowerShell 代码。

统一使用静态 Worker：

```text
Invoke-QuietModeWorker.ps1
```

支持：

```text
-Reconcile
-EnterNight
-EnterDay
-Pause
-Resume
-Status
-Diagnose
-Restore
```

通过参数文件传递结构化请求。

避免把用户配置直接拼接进 PowerShell 脚本代码。

Worker 必须输出结构化结果，包括：

- Action。
- StartTime。
- EndTime。
- ExitCode。
- 每个子模块的执行结果。
- 实际系统状态。
- ErrorCode。
- ErrorMessage。

## 7.4 静音成功判定

目前状态显示不能只检查风扇和磁盘。

完整静音成功必须验证：

| 组件 | 夜间预期状态 |
|---|---|
| FanControl | 静音配置 |
| 目标 HDD | 全部 Offline |
| 维护任务 | 全部现存目标任务 Disabled |
| Regular Maintenance | Disabled（存在时） |
| SysMain | Disabled（启用该策略时） |
| MaintenanceBoundary | 白天时间 |
| WakeUp | 0 |
| NightGate | 正常运行 |
| 调度状态 | NightActive |

所有要求的组件都验证成功才显示：

`静音已完全生效`

否则显示：

- 部分生效。
- 正在重试。
- 恢复中。
- 状态未知。
- 操作失败。

不允许仅因为 Worker 返回了请求成功就显示绿色状态。

## 7.5 风扇安全

FanControl 继续负责温度曲线和硬件控制。

QuietMode 只负责切换配置。

必须：

- 检查 FanControl IPC 是否可用。
- 检查配置文件是否存在。
- 读取切换后的实际配置。
- 失败时显示风扇状态未知。
- 不得使用不考虑温度的固定低转速替代安全风扇曲线。

---

# 8. 硬盘空闲停转模块

这个功能与夜间整盘脱机独立。

保持：

```text
SpinDownEnabled = false
```

作为默认值。

原因：大型机械硬盘可能不适合频繁启停。

功能保留：

- DISKIDLE AC/DC。
- 空闲时间设置。
- 单独开关。
- 实际值读取。
- 修改前快照。
- 恢复原始设置。

必须分别记录 AC 和 DC。

如果修改多个电源方案，则每个方案都要记录。

不要将空闲停转设置与夜间静音模式绑定。

未来可以增加可选 smartctl Adapter，但不作为 v2.0 的必需依赖。

---

# 9. 安装、卸载和旧版本迁移

## 9.1 统一入口

最终对用户提供：

```text
Install-QuietMode.ps1
Uninstall-QuietMode.ps1
Repair-QuietMode.ps1
Get-QuietModeStatus.ps1
Test-QuietMode.ps1
```

旧脚本：

```text
quiet-hdd-night.ps1
quiet-hdd-daytime.ps1
quiet-hdd-offline.ps1
quiet-hdd-pause.ps1
quiet-hdd-fix.ps1
quiet-hdd-setdisks.ps1
quiet-hdd-status.ps1
```

保留为兼容包装器。

内部调用统一 Worker，不再保留独立业务实现。

## 9.2 迁移流程

不允许直接覆盖旧安装。

步骤：

1. 检查旧版安装状态。
2. 读取旧 settings.ini。
3. 读取 diskgate.conf。
4. 读取所有旧计划任务。
5. 读取当前磁盘身份。
6. 创建旧系统完整备份。
7. 将旧磁盘编号映射成稳定身份。
8. 如果匹配不唯一，要求人工确认。
9. 生成 v2 配置。
10. 生成 v2 baseline。
11. 注册 v2 计划任务。
12. 验证 v2 Worker。
13. 执行一次 Reconcile。
14. 验证静音策略。
15. 停用旧版任务。
16. 确认新旧调度不会同时运行。
17. 最后才清理旧版任务和旧文件。

迁移失败时：

- 保留旧版配置。
- 保留旧版任务恢复信息。
- 不删除旧版脚本。
- 提供明确回滚命令。

## 9.3 卸载

卸载必须：

1. 禁止新的调度执行。
2. 联机目标硬盘。
3. 恢复维护任务原始状态。
4. 恢复 SysMain。
5. 恢复注册表。
6. 恢复电源设置。
7. 恢复 FanControl 原配置。
8. 注销本工具自己的任务。
9. 验证全部恢复。
10. 最后清理本工具文件。

不要删除其他工具使用的目录和文件。

---

# 10. 测试体系

v2.0 不允许只通过手动运行一次脚本就宣布完成。

## 10.1 PowerShell 测试

使用 Pester。

测试：

- 配置读取。
- 配置验证。
- 磁盘身份解析。
- 重复磁盘身份。
- 系统盘保护。
- 页面文件保护。
- 维护任务禁用。
- 维护任务恢复。
- Regular Maintenance 恢复。
- SysMain 恢复。
- 注册表快照。
- 电源快照。
- 回滚失败。
- 重复回滚。
- 计划任务路径。
- 时间跨午夜计算。

系统修改操作必须 Mock，不允许 CI 直接操作真实磁盘。

## 10.2 C# 测试

至少测试：

- 配置加载。
- 配置迁移。
- 状态机。
- 时间边界。
- 手动覆盖。
- 自动静音。
- Worker 结果解析。
- ProcessRunner 超时。
- 失败状态处理。
- UI 状态判定。

## 10.3 集成测试

使用 Windows 虚拟机。

通过专用虚拟磁盘测试：

1. 磁盘绑定。
2. 脱机。
3. 联机。
4. 重启。
5. 维护任务禁用。
6. 维护任务恢复。
7. UAC 取消。
8. Worker 超时。
9. 配置损坏。
10. 快照恢复。
11. 重复安装。
12. 重复卸载。

不能在开发机的真实系统盘或重要数据盘上做破坏性测试。

## 10.4 CI

GitHub Actions 使用 Windows Runner。

自动执行：

- PowerShell Parser 检查。
- PSScriptAnalyzer。
- Pester。
- C# Build。
- C# Unit Tests。
- 配置 JSON 校验。
- 发布目录完整性检查。

所有测试通过才能合并。

---

# 11. 分阶段执行计划

不要一次性让 Codex 重写全部代码。

严格按以下阶段执行。

## Phase 0：代码审计和行为基线

目标：在修改前完全理解旧版本。

工作：

- 盘点所有脚本入口。
- 盘点所有计划任务。
- 盘点注册表修改。
- 盘点电源设置修改。
- 盘点服务修改。
- 盘点 FanControl IPC。
- 盘点 C# 的全部用户功能。
- 盘点旧配置文件。
- 画出当前功能依赖图。
- 建立旧版行为基线。

产物：

- docs/LEGACY_AUDIT.md
- docs/LEGACY_BEHAVIOR.md
- docs/ARCHITECTURE.md

完成标准：

不允许有未识别的系统修改行为。

## Phase 1：统一配置和快照系统

目标：建立 v2 基础。

工作：

- 创建新目录。
- 创建统一 JSON 配置。
- 创建 ConfigValidator。
- 创建 ConfigMigrator。
- 创建 SnapshotManager。
- 实现原子写入。
- 实现 baseline。
- 实现恢复状态持久化。
- 增加配置版本兼容。

完成标准：

能够读取旧配置并转换成 v2 配置；能够生成并验证完整原始状态快照。

## Phase 2：重构磁盘管理

目标：解决所有磁盘身份和安全问题。

工作：

- 实现 DiskIdentityResolver。
- 实现唯一身份匹配。
- 移除默认 Disk 0 回退。
- 实现系统盘保护。
- 实现页面文件保护。
- 实现磁盘占用诊断。
- 实现 Offline。
- 实现 Online。
- 实现状态验证。
- 修复 Pause / Resume / Restore 的磁盘配置来源。

完成标准：

任何无法唯一匹配的磁盘都不会被操作；所有操作完成后必须验证实际状态。

## Phase 3：重构 Windows 维护管理

目标：统一维护任务策略。

工作：

- 建立唯一维护任务清单。
- 加入 Regular Maintenance。
- 加入 Crash Recovery 维护任务。
- 实现 StopTask。
- 实现 DisableTask。
- 实现 EnableTask。
- 实现 VerifyTask。
- 实现 SysMain 管理。
- 实现 MaintenanceBoundary 管理。
- 实现 WakeUp 管理。
- 实现 baseline 恢复。
- 实现任务不存在时的处理。

完成标准：

夜间所有现存目标任务禁用；白天严格恢复原始状态。

## Phase 4：统一 Worker 和调度

目标：彻底消除多套闸门。

工作：

- 创建 Invoke-QuietModeWorker.ps1。
- 创建唯一 Reconcile 入口。
- 创建统一状态机。
- 创建启动补偿。
- 创建睡眠恢复补偿。
- 创建周期校验。
- 创建手动覆盖。
- 创建 Pause / Resume / ReArm。
- 统一计划任务路径。
- 移除重复任务注册实现。

完成标准：

任意时刻只存在一个权威调度控制器，不存在 NightGate 和 DiskGate 互相覆盖状态的问题。

## Phase 5：重构 C# 托盘程序

目标：保持使用体验，改善内部架构。

工作：

- 拆分 QuietMode.cs。
- 升级到 net481。
- 重写 ProcessRunner。
- 接入统一配置。
- 接入统一 Worker。
- 接入统一状态机。
- 完善状态显示。
- 完善诊断。
- 保留所有现有菜单功能。
- 修复并发操作问题。
- 增加异常处理。

完成标准：

托盘操作和计划任务操作调用同一套业务逻辑，状态显示与实际系统状态一致。

## Phase 6：安装、迁移、恢复

目标：实现完整的 v1 到 v2 迁移。

工作：

- 创建 Install。
- 创建 Uninstall。
- 创建 Repair。
- 创建旧配置迁移。
- 创建旧计划任务发现。
- 创建旧版清理。
- 创建迁移失败回滚。
- 创建安装后自检。

完成标准：

能够从旧版本安全升级，也能完整恢复旧系统配置。

## Phase 7：测试和发布

目标：达到可以长期使用的质量。

工作：

- 完成 Pester。
- 完成 C# 测试。
- 完成 Windows CI。
- 完成虚拟机集成测试。
- 完成实际硬件验证。
- 更新 README。
- 更新 CHANGELOG。
- 更新 SAFETY 文档。
- 创建 v2.0 Release。

完成标准：

所有测试通过，迁移和恢复通过，实机夜间静音验证通过。

---

# 12. v2.0 验收标准

以下条件全部满足，才能发布。

| 编号 | 验收项目 | 必须通过 |
|---|---|---|
| A01 | 23:58 自动进入夜间模式 | 是 |
| A02 | 目标维护任务全部停止并禁用 | 是 |
| A03 | Regular Maintenance 被正确管理 | 是 |
| A04 | SysMain 夜间禁用 | 是 |
| A05 | WakeUp = 0 | 是 |
| A06 | 维护窗口调整到白天 | 是 |
| A07 | 配置硬盘全部 Offline | 是 |
| A08 | FanControl 静音配置生效 | 是 |
| A09 | 夜间重启后重新执行静音策略 | 是 |
| A10 | 睡眠恢复后重新校验 | 是 |
| A11 | 10:02 自动进入白天模式 | 是 |
| A12 | 磁盘全部 Online | 是 |
| A13 | 原始维护任务状态恢复 | 是 |
| A14 | SysMain 原始状态恢复 | 是 |
| A15 | 原始注册表设置恢复 | 是 |
| A16 | 原始电源设置恢复 | 是 |
| A17 | FanControl 常态配置恢复 | 是 |
| A18 | 自定义磁盘身份正确 | 是 |
| A19 | 不误操作系统盘 | 是 |
| A20 | 失败时保留恢复文件 | 是 |
| A21 | 重复执行恢复不会出错 | 是 |
| A22 | 暂停和重新启用调度正常 | 是 |
| A23 | UI 不会错误显示静音成功 | 是 |
| A24 | CI 自动测试通过 | 是 |

---

# 13. Codex 执行约束

把以下要求作为整个重构任务的最高级开发约束：

1. 先审计，再修改。
2. 先建立测试，再迁移功能。
3. 不要一次性删除旧脚本。
4. 不要削弱夜间严格静音策略。
5. 不要随意增加禁用 Windows 服务的范围。
6. 不要改变现有默认静音时间。
7. 不要改变早晨启动补偿行为，除非有明确测试和决策。
8. 不要使用磁盘编号作为长期身份。
9. 不允许未知配置回退到 Disk 0。
10. 不允许静默忽略系统操作失败。
11. 不允许用固定默认值代替原始配置恢复。
12. 不允许在恢复失败后删除快照。
13. 不允许多个脚本重复实现相同业务逻辑。
14. 不允许让 UI 显示未经验证的成功状态。
15. 不允许直接在开发机上执行未经验证的破坏性操作。
16. 不允许为了代码整洁而改变用户已经依赖的功能。
17. 每个 Phase 独立完成、测试、审查，再进入下一阶段。
18. 所有修改必须有日志、测试和回滚方案。

每个阶段完成后，提交：

- 修改文件清单。
- 设计变更。
- 解决的问题。
- 测试结果。
- 未解决问题。
- 下一阶段工作。

不要仅报告「代码已经写好」，必须给出实际测试证据。

---

# 14. 最终交付物

v2.0 必须包含：

1. 模块化 C# 托盘程序。
2. 统一 PowerShell Worker。
3. 统一 JSON 配置系统。
4. 稳定磁盘身份识别。
5. 严格夜间维护闸门。
6. 自动日间恢复。
7. 原始系统状态快照。
8. 故障恢复系统。
9. 统一调度器。
10. 诊断工具。
11. 安装、卸载、修复工具。
12. 旧版本迁移工具。
13. 自动化测试。
14. GitHub Actions CI。
15. 完整使用说明和安全文档。

最终目标：

**QuietMode 不再是一堆互相调用的 PowerShell 脚本，而是一套统一控制、状态可验证、修改可恢复、夜间严格执行静音策略的 Windows 管理工具。**
