# ARCHITECTURE — 现状架构与 v2 目标映射（Phase 0）

> 本文是 `LEGACY_AUDIT.md` 的结构化视图，并给出 v2 分阶段落点。
> 规格原文见 `docs/V2_SPEC.md`。本文已脱敏。

---

## 1. 现状架构（v1）

```
┌──────────────────────────── 用户层 ────────────────────────────┐
│  QuietMode.exe（单文件 2424 行 WinForms）                       │
│    Cfg（INI 读写） / Sys（系统操作） / Diag / TrayApp /         │
│    SettingsForm / BuildWorker（动态拼 PowerShell）              │
│  状态：ModeOn / SchedulePaused / AutoSilentActive /            │
│        AutoSilentSuppressed / AutoOffDayStamp（分散布尔）        │
└──────┬───────────────────┬──────────────────┬──────────────────┘
       │ 定时器 60s         │ 提权 worker.ps1   │ 用户态 IpcCtl
       │ AutoSilent         │ gate.ps1          │
       │ BootReconcile      │ spindown.ps1      │
       ▼                    ▼                   ▼
  FanControl 配置切换   6 项维护任务        FanControl IPC
  （仅风扇）            \QuietHDD\ 两任务    load / list
                         diskgate -Force*
                         DISKIDLE AC/DC

┌────────────── 计划任务层（两套并行、无仲裁） ──────────────────┐
│ \QuietHDD\QuietHDD_NightGate → C:\ProgramData\QuietHDD\gate.ps1 │
│     23:58 / 10:02 / AtStartup PT2M / SYSTEM / WakeToRun=false    │
│ \QuietHDD\QuietHDD_DiskGate  → C:\ProgramData\QuietHDD\diskgate.ps1│
│     23:58 / 10:02 / AtStartup PT2M / SYSTEM                      │
└─────────────────────────────────────────────────────────────────┘

┌────────────── 旧脚本层（7 个入口，各自实现业务） ──────────────┐
│ night.ps1 / daytime.ps1 / offline.ps1 / pause.ps1 /            │
│ fix.ps1 / setdisks.ps1 / status.ps1                            │
│ 每份脚本自带一份维护任务清单（6~7 项不等）                     │
└─────────────────────────────────────────────────────────────────┘

数据落盘（无统一模型）：
  <APPDATA>\QuietMode\settings.ini / state.ini / worker_result.txt
  C:\ProgramData\QuietHDD\diskgate.conf / *.log / spindown.orig
```

### 1.1 结构性缺陷

| # | 缺陷 | 后果 |
|---|---|---|
| S1 | 三个控制路径（NightGate / DiskGate / 托盘）+ 七个旧脚本，无唯一控制层 | 竞态，夜间策略可被反向撤销（见 LEGACY_BEHAVIOR §4） |
| S2 | 业务清单在 6 处重复定义且不一致 | Crash Recovery / Regular Maintenance 夜间禁了白天不恢复 |
| S3 | 磁盘身份 = 磁盘编号 | 换盘 / 重启后编号漂移即误操作风险 |
| S4 | C# 动态拼接 PowerShell 代码 | 无法单元测试、无参数文件、易注入配置值 |
| S5 | Worker 结果纯文本 key=value，且只报 `*_REQUESTED` | 无法验证实际系统状态 |
| S6 | 无 baseline 全量快照 | 白天恢复用硬编码默认值，破坏用户原始配置 |
| S7 | 状态为分散布尔字段 | 无优先级、无降级态、无恢复态 |
| S8 | `ReadToEnd` + `WaitForExit` 顺序 | 大输出可能死锁托盘 UI |
| S9 | 配置 INI 无校验 / 非原子写 | 损坏即失控 |
| S10 | `Stop-Process -Force defrag` 无差别 | 违反方案 §4.2 |

---

## 2. v2 目标架构（来自规格）

```
┌─────────────────────────────────────────────────────────┐
│ src/QuietMode.App/        UI、托盘、菜单、设置、状态显示   │
│ src/QuietMode.Core/       唯一业务控制层                   │
│   Models/      Config / State / DiskIdentity /            │
│               MaintenanceTask / OperationResult           │
│   Services/   QuietModeController / ScheduleEngine /      │
│               StateMachine / RecoveryManager /            │
│               DiagnosticService                          │
│   Config/     ConfigStore / ConfigValidator / Migrator    │
│ src/QuietMode.Windows/    Windows 适配层                  │
│   DiskManager / DiskIdentityResolver / MaintenanceManager│
│   ScheduledTaskManager / ServiceManager / PowerPlanManager│
│   FanControlClient / ProcessRunner / SnapshotManager /   │
│   FileHandleInspector                                    │
├─────────────────────────────────────────────────────────┤
│ powershell/Modules/*.psm1   8 个模块                     │
│ powershell/Invoke-QuietModeWorker.ps1  唯一静态 Worker    │
│   -Reconcile / -EnterNight / -EnterDay / -Pause /        │
│   -Resume / -Status / -Diagnose / -Restore                │
│ powershell/Install / Uninstall / Repair / Get-Status / Test│
├─────────────────────────────────────────────────────────┤
│ config/default-policy.json                               │
│ C:\ProgramData\QuietMode\                                │
│   config.json / state.json / baseline.json /              │
│   config.backup.json / logs\ / recovery\                  │
│ <APPDATA>\QuietMode\ui-settings.json（仅 UI 偏好）        │
├─────────────────────────────────────────────────────────┤
│ 唯一调度：\QuietMode\QuietMode-Reconcile                  │
│   23:58 / 10:02 / AtStartup+2min / 睡眠恢复 / 解锁 /     │
│   夜间每 10 min（不唤醒、不并发、有超时）                 │
└─────────────────────────────────────────────────────────┘
```

原则：C# 管 UI 与业务决策；Windows 适配层管具体系统操作；PowerShell 管原生管理接口兼容；**所有模块共享一份配置、一份原始状态快照、一份维护任务清单**。

目标框架 `net481`（保留 Win10 22H2 / Win11 兼容，不引入现代 .NET Runtime）。

---

## 3. 现状 → v2 映射表

| 现状 | v2 落点 | Phase |
|---|---|---|
| `Cfg` INI 读写 | `QuietModeConfig` + `ConfigStore` + `ConfigValidator` + `ConfigMigrator` | 1 |
| `spindown.orig` 8 B | `SnapshotManager` → `baseline.json`（任务/服务/注册表/电源/磁盘/风扇/元数据） | 1 |
| `state.ini` 五个布尔 | `StateMachine` + `state.json`（10 个状态、集中转换、优先级） | 4 |
| `diskgate.conf` `Disks=0,1` | `DiskIdentity`（UniqueId/Serial/Model/Size/Bus）+ `DiskIdentityResolver` | 2 |
| `diskgate.ps1` 内联安全锁 | `DiskManager` + `FileHandleInspector`，结构化 `DiskOperationResult` | 2 |
| `Cfg.MaintPaths/MaintNames` + 5 份脚本副本 | 唯一 `MaintenanceTask` 清单（含 Regular Maintenance / Crash Recovery） | 3 |
| `night.ps1` 的 SysMain / Boundary / WakeUp | `ServiceManager` + 注册表快照恢复（按原始存在状态） | 3 |
| `gate.ps1` / `diskgate.ps1` / `worker.ps1` | `Invoke-QuietModeWorker.ps1` + 8 个 psm1 | 4 |
| `\QuietHDD\` 两个任务 | `\QuietMode\QuietMode-Reconcile` 单一任务 | 4 |
| `Sys.Run` 动态拼接 | `ProcessRunner`（并行异步读 stdout/stderr、真超时、区分启动失败/UAC 取消/超时） | 2 |
| `QuietMode.cs` 单文件（2424 行） | `Program` + `App` + `Core`（qmctl 契约）+ `Ui` + `Diag`，业务逻辑全部移出托盘 | 5 |
| 7 个旧脚本 | 兼容包装器 → 统一 Worker | 6 |
| 无安装器 | `Install` / `Uninstall` / `Repair` + 迁移 + 回滚 | 6 |
| 无测试 | Pester / C# 单测 / VM 集成 / GitHub Actions Windows CI | 7 |

---

## 4. 分阶段路线（严格串行，每阶段独立验收）

| Phase | 目标 | 关键产物 | 完成标准 |
|---|---|---|---|
| **0** | 代码审计与行为基线 | `LEGACY_AUDIT.md` / `LEGACY_BEHAVIOR.md` / `ARCHITECTURE.md` / `V2_SPEC.md` | 未识别的系统修改 = 0 ✅ |
| 1 | 统一配置与快照 | `config.json` / `baseline.json` / 原子写 / 迁移读取 | 能读旧 ini+conf 转 v2 配置；能生成并验证完整快照 |
| 2 | 磁盘管理 | `DiskIdentityResolver` / 唯一匹配 / 系统盘与页面文件保护 / 实状态验证 | 无法唯一匹配的盘绝不操作；操作后必验实际状态 |
| 3 | 维护管理 | 唯一清单（8 项）/ Stop+Disable+Verify / SysMain / Boundary / WakeUp / baseline 恢复 | 夜间全部现存目标 Disabled；白天严格恢复原始状态 |
| 4 | 统一 Worker 与调度 | 静态 Worker / 单一 Reconcile / 状态机 / 启动与睡眠补偿 / 周期校验 / Pause-ReArm | 任意时刻只有一个权威调度控制器 |
| **5** | C# 托盘重构 | 分层拆分（App/Core/Ui/Diag）/ 只认 `qmctl status` 的 JSON / 删除 `Apply` 与 `CheckAutoSilent` | ✅ 托盘与计划任务调用同一套业务逻辑；托盘不再有第二份策略 |
| 6 | 安装迁移恢复 | Install / Uninstall / Repair / 迁移 / 回滚 / 安装后自检 | 能从 v1 安全升级并完整恢复旧系统配置 |
| 7 | 测试与发布 | Pester / C# 测试 / Windows CI / VM 集成 / README / CHANGELOG / SAFETY / v2.0 Release | 全部测试通过，实机夜间验证通过 |

约束（规格 §13）：先审计再修改；先建测试再迁功能；不一次性删旧脚本；不削弱夜间严格策略；不扩大服务禁用范围；不改默认时间；不改开机补偿；不用磁盘号做长期身份；不回退 Disk 0；不静默忽略失败；不用固定默认值代替原始恢复；恢复失败不删快照；不重复实现业务逻辑；不显示未验证成功；不在开发机做破坏性测试；不为整洁改变已有功能；每阶段独立提交与验收；所有修改有日志、测试、回滚方案。

---

## 5. 迁移安全要点（Phase 6 前置）

1. 不直接覆盖旧安装；先备份旧 `settings.ini`、`diskgate.conf`、全部 `\QuietHDD\` 任务定义、当前磁盘身份。
2. 把磁盘编号映射成稳定身份；**匹配不唯一时要求人工确认**，绝不自动回退 Disk 0。
3. 生成 v2 配置与 v2 baseline 后，注册 v2 任务 → 验证 Worker → 执行一次 Reconcile → 验证静音策略。
4. 最后才停用并清理旧任务与旧脚本，且确认新旧调度不会同时运行。
5. 迁移失败保留旧配置、旧任务恢复信息、旧脚本，并提供明确回滚命令。
6. 卸载顺序：禁新调度 → 联机 → 恢复任务/服务/注册表/电源/风扇 → 注销任务 → 验证全部恢复 → 最后清理文件；不递归删除公共目录。

---

## 6. Phase 0 结论

- 现状所有系统修改行为已识别（14 类、无遗漏）。
- 10 项结构性缺陷已定位，其中 **B01 / B03 / B04 / B05 / B06** 直接影响夜间静音正确性与用户配置安全。
- 已满足且不得回退的两项：系统盘/页面文件保护、DISKIDLE 独立性。
- 下一阶段 Phase 1 只建立配置与快照基础设施，**不改动任何运行时系统状态**，可在实机安全执行。
