# 变更记录

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
