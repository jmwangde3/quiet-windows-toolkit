# 变更记录

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
