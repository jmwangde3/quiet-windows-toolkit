# 变更记录

## 2.0.0-phase6.2 — 2026-11-12

### 新增：reconcile 心跳（`logs/reconcile.log`）

- 每次 reconcile 都追加一行，**包括决策为 `none` / `verify` 的那种**：
  `时间 trigger=… action=… apply=0/1 verdict=… state=…`
- 修的是一个观测盲区：无事可做时既不写请求文件、也不写 `worker.log`，
  于是「任务没触发」和「触发了但没活干」在磁盘上完全同形。首夜之后 00:28 / 00:38
  两次「消失」就是这么来的 —— 查无可查。
- 超过 2 MB 自动滚到 `reconcile.log.1`。
- `qmctl task status` 增加「调度心跳」一行与 JSON 字段 `heartbeat` / `heartbeatMinutesAgo`。

### 修复：`task status` 误报重复调度

- `\QuietHDD\` 两个闸门在 ACL 挡住时 `Exists=true / Enabled=null`，
  旧逻辑把 `Enabled` 为 null 当成「仍在启用」，于是每次都输出「存在重复调度」。
  现在算「无法确认」，按「可能有重复调度」处理但不再断言。

## 2.0.0-phase6.1 — 2026-11-12

`DISKIDLE` 进入 reconcile 链。**这是一次会改系统状态的变更**：首轮 reconcile 会把
`DISKIDLE` 从实测的 300 秒写成配置里的 1200 秒。

### 修正

- **配置写 20 分钟、系统里却是 300 秒**。`spinDown` 从 Phase 5 起只是「独立开关」，
  `reconcile` 链根本不碰它，于是 `config.json` 与电源方案长期分叉，谁也没发现。
  现在 `spinDown.enabled=true` 时 `spindown` 是夜间/白天链上固定一步：
  `ReconcilePlanner` 排步 → worker 调 `Invoke-QmSpinDown` 写 AC/DC + `setactive`
  → C# 用 `PowerPlanReader` 独立回读 → 不一致判 `Failed`。`enabled=false` 时整步
  `skipped`，一个字节都不写。

### 新增

- `powershell/Modules/QuietMode.Power.psm1`：`Get-QmDiskIdleSeconds`（`powercfg /query`
  解析十六进制索引，失败读注册表）、`Invoke-QmSpinDown`（已是目标值则跳过 `setactive`，
  写完回读，只认回读值）。
- `ReconcileModels.SpinDownObservation`：`expectedSeconds` / `acSeconds` / `dcSeconds` /
  `captured` / `matches`，DC 只在读得到时参与判定。
- `qmctl spin show` 增加 `expectedSeconds` / `reconcileManaged` / `matches`，
  文本模式直接点出「配置已启用，next reconcile 会拉回 Ns」。
- `qmctl spin set` 在配置已启用且数值不同时提示会被 reconcile 覆盖，并给出
  `config set spinDown.minutes` 的正确用法。
- 单测 83 → 86（夜间/白天五步顺序、停转开启/关闭、`DISKIDLE` 不符判 Failed、读不到判 Degraded）。

## 2.0.0-phase5 — 2026-11-11

托盘重构：从“什么都干”的单文件变成 `qmctl` 的前端。**不改变运行中的系统状态**：
本阶段只动代码与只读探测，未执行任何 `--apply`。

### 新增

- `QuietMode/` 分层（原 2424 行 `QuietMode.cs` 已删除）：
  - `Program.cs`：入口、单实例、`--diag` / `--status` / `--selftest`。
  - `App/AppPaths.cs`、`App/AutoStart.cs`、`App/TrayLog.cs`：路径与 qmctl 定位、HKCU 自启与托盘常显、日志。
  - `Core/QmctlClient.cs`：唯一的 qmctl 调用通道（含 `runas` 提权与 UAC 取消识别）。
  - `Core/MiniJson.cs`：只读 JSON 解析，托盘不引用 `QuietMode.Core.dll`。
  - `Ui/TrayApp.cs`、`Ui/SettingsForm.cs`、`Ui/Icons.cs`：菜单、设置、图标。
  - `Diag/Diagnostics.cs`：自检输出。
- `qmctl status [--json]`：一次性观测（窗口、决策、控制器状态、风扇、磁盘、停转、计划任务、baseline）。
- `qmctl apply <manual-quiet|manual-normal|auto|pause|resume|rearm> [--apply]`：
  一次用户动作 = 一次提权 = 一次 reconcile，意图与结果分离。
- `qmctl config set <key> <value>`：白名单键 + 类型先挡 + `ConfigStore` 强校验；
  `--force` 跳过文件存在性检查。
- `qmctl spin show` / `qmctl spin set <分钟> [--apply]` + `SpinDownController`：
  DISKIDLE 独立开关，`powercfg` 写入后独立回读（`powercfg /query` 优先，注册表兜底）。
- `docs/TRAY.md`：分层、JSON 契约、图标语义、失败语义、已知取舍。
- 单测新增 5 项（`SystemClock`、`IsLastRunFailed`、状态快照契约）；
  `Test-QuietMode.ps1` 新增“编译托盘”“托盘契约：status 字段齐全（只读）”两步。

### 修正

- **开机超过 24.9 天后夜间窗口会被误判成白天**。`Environment.TickCount & 0x7FFFFFFF`
  回绕后算出的“运行时长”变成几毫秒，落进 `schedule.startupGraceMinutes`（15 分钟），
  于是 06:00–10:02 被判为“刚开机、按白天处理”。改用 `GetTickCount64`（`SystemClock.Uptime()`），
  `ReconcileController` 与 `StatusProbe` 共用。
- **`IsLastRunFailed` 有两份实现**（ReconcileController 私有 + 托盘将要有自己的）。
  收进 `StateMachine.IsLastRunFailed`，`NotRun` 明确不算失败。
- **托盘状态显示不再“看不见就当正常”**：`offline` / `exists` 读不到时 JSON 给 `null`，
  界面显示“状态未知”；计划任务未注册、v1 闸门仍启用/读不到都变成常驻警告。

### 移除

- 托盘里的 `Apply()`、`CheckAutoSilent()`、`BootReconcile()`、`Sys.BuildWorker/BuildGateScript/BuildSpinScript`、
  `--fixgate`、`--fixhwinfo`：这些行为全部由 `qmctl reconcile` 与计划任务接管。
- 托盘对 `settings.ini` / `state.ini` 的读写：配置与状态只剩 `config.json` / `state.json` 一个写入口。
- 托盘的 `System.Management` 依赖（WMI 探测移到 `qmctl status`）。

### 升级注意

- 托盘现在需要同目录的 `qmctl.exe`（或 `tray.ini` 的 `QmctlPath`）。找不到时菜单全部禁用并提示，
  **不会**退回旧逻辑。
- 每次切换静音模式会弹一次 UAC：v1 在“夜间自动静音中手动关闭”时不弹，v2 为了“意图+执行+验证”
  原子化接受这个代价（见 `docs/TRAY.md` §7）。
- 自动切换仍依赖 `\QuietMode\QuietMode-Reconcile`；本阶段**未注册**该任务（Phase 6 做迁移安装），
  所以当前运行环境下自动切换不会发生 —— 托盘会在菜单第三行常驻提示。

### 测试

- 单测 81 项全过。
- `Test-QuietMode.ps1` 全绿：编译 3 项、单测、配置校验、托盘契约、磁盘安全 5 项、维护安全 6 项、调度安全 10 项。
- 实机只读验证：`qmctl status` 报 `DayActive` / `action=none`，风扇 `disabled`（临时 root 默认策略）、
  DISKIDLE `AC=300s DC=0s`；`task status` 正确报“v2 任务不存在 + v1 闸门可能仍在”。
- 托盘冒烟：`--selftest` 三条路径（正常配置 / 配置缺失 / qmctl 缺失）均无异常退出，日志无堆栈。

## 2.0.0-phase4 — 2026-11-11

统一 Worker 与调度。**不改变运行中的系统状态**：默认演练，`--apply` 才动手，未提权必然失败。

### 新增

- `src/QuietMode.Core/Scheduling/`
  - `ScheduleWindow.cs`：完整 `HH:mm` 的跨午夜窗口判定 + 显式开机补偿，带原因输出。
  - `ControllerState.cs`：10 个控制器状态 + `StateMachine`（唯一决策点）+ `ApplyRun`（唯一状态写回点）。
  - `ReconcileModels.cs`：reconcile 计划 / 步骤结果 / 磁盘与风扇观测 / 三档验证结论。
  - `ReconcilePlanner.cs`：夜间与白天两条固定顺序 + `ReconcileVerifier`（全组件验证，缺一项不报成功）。
- `src/QuietMode.Windows/ReconcileController.cs`：决策 → 计划 → 调 Worker → 复观测 → 独立验证 → 写回状态。
- `src/QuietMode.Windows/SchedulerInstaller.cs`：`\QuietMode\QuietMode-Reconcile` 的 XML 生成、注册、卸载、v1 闸门检查与停用。
- `src/QuietMode.Windows/Native/FanProbe.cs`：`IpcCtl.exe list` 解析 `current=`，风扇配置真实回读。
- `powershell/Worker/Invoke-QuietModeWorker.ps1`：唯一执行器（`-Reconcile/-EnterNight/-EnterDay/...` 统一到请求 JSON）。
- `powershell/Modules/QuietMode.Fan.psm1`：风扇切换唯一写入口，切换后回读比对。
- `powershell/Test-SchedulingSafety.ps1`：只读集成测试 9 项。
- `qmctl reconcile`、`qmctl task status|print|install|uninstall|disable-legacy`、`qmctl state pause|resume|rearm|manual-quiet|manual-normal|auto`。
- `docs/SCHEDULING.md`。

### 修正

- **风扇状态不再靠猜**。v1 注释写「FanControl 未暴露当前配置名」，实测 `IpcCtl.exe list` 直接返回 `current=<文件名> folder=<目录>`，v2 用它做回读验证。
- **v1 闸门读取失败不再等于「没有冲突」**。`\QuietHDD\` 的 ACL 让普通用户读不到任务，`task status` 现在把「读不到」判为可能冲突，而不是报「没有重复调度」。
- `QuietModeState` schema 3：新增 `lastVerdict` / `lastErrorCode` / `lastReconcileAction` / `consecutiveFailures` / `controllerState`。

### 测试

- 单测 76 项（新增 16 项：时间窗口 3、状态机 6、计划顺序 3、验证 4）。
- `Test-SchedulingSafety.ps1` 9 项全过，并挂进 `Test-QuietMode.ps1`。
- 实机：`reconcile` 演练在 22:47 正确判为白天并给出 `DayActive`；`reconcile night` 演练输出 4 步计划与逐项失败原因（7 个任务仍启用、SysMain `Start=2`、两块盘 Online、风扇当前 `FanControl.json`），全程未改动任何状态。

## 2.0.0-phase3 — 2026-11-11

Windows 维护管理重构。**不改变运行中的系统状态**：默认演练，`--apply` 才动手，未提权必然失败。

### 新增

- `src/QuietMode.Core/Maintenance/`
  - `MaintenanceObservation.cs`：一次系统观测（任务 / 服务 / 注册表），`Present` / `NotPresent` / `Unknown` 三态。
  - `MaintenancePlan.cs`：步骤模型（8 种动作）+ 5 种执行状态，新增 `NotRun` 区分「没跑」与「跳过」。
  - `MaintenancePlanner.cs`：纯计划器，把规格 §4.2 / §4.3 的顺序固化成步骤列表。
  - `MaintenanceVerifier.cs`：纯验证器，输出 `Ok` / `Degraded` / `Failed`。
- `src/QuietMode.Windows/MaintenanceProbe.cs`：只读观测（Task Scheduler COM / 服务注册表 / 维护注册表）。
- `src/QuietMode.Windows/MaintenanceController.cs`：出计划 → 脚本执行 → **重新观测并独立验证**。
- `powershell/Modules/QuietMode.Maintenance.psm1`：每个写动作 `-ErrorAction Stop` + 写后复读。
- `powershell/Maintenance/Invoke-MaintenanceOperation.ps1`：维护写操作唯一入口。
- `powershell/Test-MaintenanceSafety.ps1`：只读集成测试（清单三态、计划只读、未提权必须失败）。
- `qmctl maint list|status|plan|night|day|verify|baseline-enable|baseline-disable`。
- `docs/MAINTENANCE.md`。
- `MaintenanceCatalog.SelfCheck()`：清单自检，禁止条目落进 Windows Update / Defender / Firewall 等禁改范围。

### 修正（相对 v1）

- v1 的六处 `$MaintTasks` 数组漏掉 `Data Integrity Scan for Crash Recovery`，还重复了一条 `Data Integrity Scan`；v2 只有一处清单，且有自检。
- `Regular Maintenance` 在本机**不存在**，v1 的"夜间禁用"一直是空操作；v2 按 `NotPresent` 处理并在输出里明说。
- v1 只禁不恢复（`Regular Maintenance` 尤其）；v2 白天按 baseline 恢复，baseline 没记录的值一律不写。
- v1 直接杀进程解决问题；v2 只按任务路径 `Stop-ScheduledTask`，不碰同名进程。
- v1 靠"命令没报错"判断成功；v2 每次写后复读，再由 C# 独立复观测定结论。

### 验证

- 单测 60 项全过（新增 17 项：清单自检 2、夜间计划/验证 5、白天计划/验证 7、状态语义 3）。
- `Test-QuietMode.ps1` → `ALL PASS`（新增「维护管理（只读集成测试）」「维护计划与验证」两步）。
- 实机：`maint plan night` 11 步、`maint plan day` 2 步、`maint verify night` → `Failed`（7 个任务仍启用 + SysMain 未禁用，符合当前真实状态）；`maint night --apply` 未提权 → 全部步骤标 `[未执行]`、退出码 2。
- 顺带修正 Phase 2 遗留的一处测试脆弱点：数据盘被占用时 `SAFETY_BLOCKED` 先于 `ELEVATION_REQUIRED` 生效，原断言只认后者。

## 2.0.0-phase2 — 2026-11-11

磁盘管理重构。**不改变任何运行中的磁盘状态**：默认演练，`--apply` 才动手，且未提权必然失败。

### 新增

- `src/QuietMode.Core/Disks/`
  - `DiskIdentityResolver.cs`：UniqueId 优先 / 序列号兜底 / 容量与总线复核；五种匹配状态；磁盘编号不参与匹配。
  - `DiskSafetyGuard.cs`：系统盘、启动盘、页面文件、存储池、状态未知一律硬阻断；force 仅越过「占用」。
  - `DiskOperationResult.cs`：规格 §3.4 的结构化结果，`Complete()` 强制「回读一致才算成功」。
  - `DiskOccupancy.cs`：占用诊断模型。
- `src/QuietMode.Core/Processes/ProcessRunner.cs`：并行读 stdout/stderr、真超时、超时杀进程树、区分启动失败/超时/非零退出。
- `src/QuietMode.Windows/DiskController.cs`：写请求 JSON → 调 PowerShell → 读结果 JSON。
- `src/QuietMode.Windows/Native/DiskProbe.cs`：卷 / 页面文件 / 转储文件 / 存储池 / I-O 计数器 / 进程线索只读探测。
- `powershell/Modules/QuietMode.Disk.psm1`：身份解析、安全闸门、`Set-Disk` + 回读、占用诊断。
- `powershell/Disks/Invoke-DiskOperation.ps1`：磁盘写操作的唯一入口（落盘前二次复检）。
- `powershell/Test-DiskSafety.ps1`：只读集成测试（系统盘必须被拦住、未提权不得报成功）。
- `qmctl disks resolve|inspect|offline|online|verify`。
- `docs/DISK_MANAGEMENT.md`。

### 修正（相对 v1）

- v1 的 `diskgate.ps1` 直接按磁盘编号 `Disks=0,1` 操作；v2 只按稳定身份匹配，且身份不唯一时拒绝执行。
- v1 只把「进程路径正则」当作占用检测；v2 以物理磁盘 I/O 计数器为主、进程线索为辅，并明确区分「无法判定」与「空闲」。
- v1 的 PowerShell 调用用 `ReadToEnd()` + `WaitForExit(timeout)`，存在死锁与假超时；v2 的 `ProcessRunner` 并行读取并真正终止超时进程树。
- `MSFT_Disk.HealthStatus` 是数值枚举，v1 直接转字符串得到 `0`；v2 映射为 `Healthy` / `Warning` / `Unhealthy`。
- 性能计数器实例名对无挂载卷的磁盘只有编号（`0` 而非 `0 C:`），按编号前缀匹配会漏判 —— 已修。

### 验证

- 单测 43 项全过（新增 22 项：身份解析 10、安全闸门 10、结果不变式 2）。
- `Test-QuietMode.ps1` → `ALL PASS`（含新增的「磁盘身份解析」「磁盘安全闸门」两步）。
- 实机：5 块盘全部唯一匹配；系统盘 `inspect --force` 仍判「禁止脱机」，`offline --apply --force` 返回 `SAFETY_BLOCKED`；数据盘演练不被误拦；未提权 `--apply` 返回 `ELEVATION_REQUIRED`。

## 2.0.0-phase1 — 2026-11-11

统一配置与快照系统。**不修改运行中的系统状态**，v1 脚本与托盘程序保持原样可用。

### 新增

- `src/QuietMode.Core/`
  - `Json/Json.cs`：零依赖、保序 JSON 读写（严格 RFC 8259 子集，错误带行列号）。
  - `Models/QuietModeConfig.cs`：v2 配置模型 + 默认策略。
  - `Models/QuietModeState.cs`：运行状态 + 恢复事务进度。
  - `Models/BaselineSnapshot.cs`：原始状态快照模型。
  - `Models/MaintenanceTask.cs`：**唯一**维护任务清单（`MaintenanceCatalog`）。
  - `Models/SnapshotIntegrity.cs`：快照完整性校验值。
  - `Models/OperationResult.cs`：统一操作结果（区分「已验证」与「仅请求」）。
  - `Configuration/ConfigStore.cs` / `ConfigValidator.cs` / `ConfigMigrator.cs` / `AtomicFile.cs` / `QuietModePaths.cs`。
- `src/QuietMode.Windows/`
  - `SnapshotManager.cs`：baseline 采集、保存、校验。
  - `Native/`：磁盘（MSFT_Disk）、计划任务（Task Scheduler COM）、服务、注册表、电源方案、Windows 版本、FanControl 只读探测。
- `src/QuietMode.Cli/`：`qmctl`，支持 `config` / `snapshot` / `state` / `disks` / `paths`。
- `powershell/Modules/QuietMode.Common.psm1`、`QuietMode.Config.psm1`，`Get-QuietModeStatus.ps1`、`Test-QuietMode.ps1`。
- `config/default-policy.json`、`QuietMode.sln`、`build/build.ps1`。
- `tests/QuietMode.Core.Tests/`：21 项单测（JSON、校验、原子写、last-known-good、迁移、state、快照完整性、任务清单）。
- `docs/CONFIGURATION.md`。

### 修正（相对 v1）

- DISKIDLE 设置 GUID 按实机核对改为 `6738e2c4-e8a5-4a42-b16a-e040e769756e`；此前的猜测值读不到真实电源设置，只能靠 `powercfg` 兜底。
- 计划任务读取改走 COM，避免 `schtasks` 控制台编码把中文名与 XML 弄坏；非提权读不到受保护任务时明确标 `exists=null` + "拒绝访问"，不再当成"不存在"。
- 无法解析稳定身份的磁盘不再写入配置（v1 用磁盘编号，无回退保护）。

### 兼容

- v1 文件（`settings.ini` / `state.ini` / `diskgate.ps1` / 托盘 exe）行为不变；`qmctl config migrate` 是只读迁移，写目标由 `--root` 决定。
