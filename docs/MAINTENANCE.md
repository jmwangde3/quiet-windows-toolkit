# Windows 维护管理（Phase 3）

夜间静音的核心不是"把磁盘设成脱机"，而是**让 Windows 不要在该安静的时候动磁盘**。
Windows 的自动维护、碎片整理、磁盘清理、完整性扫描、搜索索引维护都会在夜间被唤醒。
这份文档说明 v2 怎么管它们，以及为什么这么管。

相关代码：

| 位置 | 职责 |
|---|---|
| `src/QuietMode.Core/Models/MaintenanceTask.cs` | **唯一**维护任务清单 + 禁改范围自检 |
| `src/QuietMode.Core/Maintenance/MaintenancePlanner.cs` | 纯计划器：配置 + baseline + 观测 → 步骤列表 |
| `src/QuietMode.Core/Maintenance/MaintenanceVerifier.cs` | 纯验证器：观测 + baseline → Ok / Degraded / Failed |
| `src/QuietMode.Windows/MaintenanceProbe.cs` | 只读观测（COM 任务 / 服务 / 注册表） |
| `src/QuietMode.Windows/MaintenanceController.cs` | 出计划 → 脚本执行 → **重新观测并独立验证** |
| `powershell/Maintenance/Invoke-MaintenanceOperation.ps1` | 唯一写入入口 |
| `powershell/Modules/QuietMode.Maintenance.psm1` | 每个写动作都带写后复读 |

## 1. 唯一清单

清单只在一处定义。任何脚本、Worker、托盘程序都必须从这里取，不允许再各自维护 `$MaintTasks` 数组
（v1 有六处各自定义，漏掉了 Crash Recovery，还重复了一条 Data Integrity Scan）。

| 模块 | 任务 | 夜间默认停用 |
|---|---|---|
| night-maintenance | `\Microsoft\Windows\Defrag\ScheduledDefrag` | 是 |
| night-maintenance | `\Microsoft\Windows\DiskCleanup\SilentCleanup` | 是 |
| night-maintenance | `\Microsoft\Windows\Data Integrity Scan\Data Integrity Check And Scan` | 是 |
| night-maintenance | `\Microsoft\Windows\Data Integrity Scan\Data Integrity Scan` | 是 |
| crash-recovery | `\Microsoft\Windows\Data Integrity Scan\Data Integrity Scan for Crash Recovery` | 是 |
| night-maintenance | `\Microsoft\Windows\Chkdsk\ProactiveScan` | 是 |
| night-maintenance | `\Microsoft\Windows\Shell\IndexerAutomaticMaintenance` | 是 |
| night-maintenance | `\Microsoft\Windows\TaskScheduler\Regular Maintenance` | 是（本机不存在） |
| crash-recovery | `\Microsoft\Windows\Windows Error Reporting\QueueReporting` | 否 |
| gate | `\QuietHDD\QuietHDD_NightGate` | 否（自己的闸门，永不停用） |
| gate | `\QuietHDD\QuietHDD_DiskGate` | 否（自己的闸门，永不停用） |

**本机实测（2026-11-11）**：`Regular Maintenance` 在本机**不存在**，v1 那句"夜间禁用 Regular Maintenance"
一直是空操作；`Data Integrity Scan for Crash Recovery` 存在且默认启用，而 v1 清单整个漏掉了它。

## 2. 禁改范围（规格 §4.6）

下列路径前缀由 `MaintenanceCatalog.ForbiddenPrefixOf()` 硬性拦住，清单里写进去也会被计划器跳过并记录：

```
\Microsoft\Windows\WindowsUpdate\
\Microsoft\Windows\Defender\
\Microsoft\Windows\Windows Defender\
\Microsoft\Windows\Firewall\
\Microsoft\Windows\CertificateServicesClient\
\Microsoft\Windows\BitLocker\
```

`MaintenanceCatalog.SelfCheck()` 在单测里跑，保证清单本身没踩到这些范围。

## 3. 夜间顺序

计划器产出的顺序就是执行顺序，不存在"脚本自己决定先做什么"：

```
保存/确认 baseline（由调用方保证，缺失则拒绝进入夜间）
  ↓
StopTask      仅对该任务路径 + 名称，不按进程名杀 defrag.exe
  ↓
DisableTask   Disable-ScheduledTask -ErrorAction Stop
  ↓
StopService   SysMain（非关键，失败只降级）
  ↓
SetServiceStart  SysMain = 4（Disabled）
  ↓
SetRegistryValue  MaintenanceBoundary = maintenanceHour
SetRegistryValue  WakeUp = 0
  ↓
（磁盘 Offline / FanControl 由 Phase 2、Phase 4 负责接进来）
  ↓
重新观测 + 验证 → NightActive / Degraded / Failed
```

注意：`StopTask` 只在观测到 `State=Running` 时才进计划。任务没在跑就不产生"停止"步骤，
避免拿 `Stop-ScheduledTask` 去撞一个本来没运行的任务然后报错。

## 4. 白天恢复顺序

```
SetTaskEnabled   每个任务恢复到 baseline 记录的状态
  ↓
SetServiceStart  SysMain 恢复到 baseline 的 Start 值
StartService     仅当 baseline 记录当时是 Running
  ↓
SetRegistryValue / DeleteRegistryValue
                 MaintenanceBoundary、WakeUp 按 baseline 的「存在与否 + 类型 + 数据」恢复
  ↓
重新观测 + 验证 → DayActive / Degraded / Failed
```

关键原则：**恢复的是 baseline，不是 Windows 默认值。**

- baseline 记录原本启用 → 恢复启用；
- baseline 记录原本禁用 → 保持禁用，不擅自打开；
- baseline 记录该注册表值原本不存在 → `DeleteRegistryValue`，而不是写 `0`；
- baseline 没有记录 → 计划里写一条说明，**不写回**（宁可不改，也不编造原始值）。

## 5. 三种结论

| 结论 | 含义 | 判据 |
|---|---|---|
| `Ok` / `NightActive` / `DayActive` | 目标全部达成 | 无 Problems、无 Warnings |
| `Degraded` | 达成了但有隐患 | 有 Warnings，例如任务已禁用但仍在 Running、读取失败无法确认 |
| `Failed` | 目标没达成 | 有 Problems，例如任务仍然启用、注册表值没写进去 |

`Degraded` 会触发重试（Phase 4 的 Worker 负责调度），`Failed` 不允许把状态标成已静音。

## 6. 三种存在性

| 状态 | 语义 | 处理 |
|---|---|---|
| `Present` | 任务存在 | 正常参与计划与验证 |
| `NotPresent` | 任务确实不存在（`0x80070002`） | 跳过，**不计为失败**，计划里留说明 |
| `Unknown` | 读不到（多半是 `0x80070005` 权限） | 仍尝试操作，但验证会记 `Degraded`，绝不假装成功 |

`\QuietHDD\` 目录 ACL 受限，普通用户读这两个任务会得到 `Unknown` —— 这是设计上的诚实行为，
不是 bug。以管理员身份运行即可读到。

## 7. 命令行

```powershell
qmctl maint list                        # 清单 + 当前存在/启用状态
qmctl maint status                      # 配置 + baseline 对照 + 需要你决定的异常
qmctl maint plan night                  # 只算计划，不动系统
qmctl maint plan day
qmctl maint night                       # 演练（默认不动手）
qmctl maint night --apply               # 真正执行（需要管理员）
qmctl maint day --apply
qmctl maint verify night                # 只读验证当前是否达成
qmctl maint verify day
qmctl maint baseline-enable 3 --apply   # 把 baseline 里第 3 条纠正为「启用」
qmctl maint baseline-disable all --apply
```

`--json --out <file>` 输出结构化结果，供 PowerShell 模块读取（避开控制台代码页 936）。

## 8. baseline 异常：快照拍在"已禁用"之后

如果 baseline 是在旧版本把任务禁用之后拍的，里面会记成 `enabled=false`，
于是白天只会"恢复到禁用"，等于这些维护任务永远开不起来。

`qmctl maint status` 会把这类条目单独列出来：

```
需要你决定的 baseline 异常：
  - \Microsoft\Windows\Defrag\ScheduledDefrag 在 baseline 里就是「禁用」，白天不会恢复启用
    （若这不是你的本意，说明快照拍在旧版本已禁用之后）
  修正方式：qmctl maint baseline-enable <序号> --apply
```

`baseline-enable` 会重算完整性哈希并写回，属于显式的人工决定，不会被自动执行。

## 9. 本机观察到的状态漂移（2026-11-11 22:31:43）

实测到一次无法归因的外部变更：7 个维护任务在同一秒被重写为 `Enabled=true`、`State=Ready`，
而我们这边没有任何写操作（当时的 `qmctl` 调用全是只读）。同一时刻四个任务文件
（`ScheduledDefrag` / `SilentCleanup` / `ProactiveScan` / `IndexerAutomaticMaintenance`）
的 `LastWriteTime` 全部是 `22:31:43`。

结论不是"找出是谁干的"，而是**任务启用状态对外部世界来说是可变的**：

1. 夜间禁用后必须复读，不能凭"Disable 没报错"收工；
2. 白天恢复必须依据 baseline，不能依据"上一次运行我们改成了什么"；
3. 任何时刻的 `maint verify` 结论只对那一刻成立。

## 10. 已知限制

- **写操作需要管理员**。未提权时 `--apply` 返回 `ELEVATION_REQUIRED`，步骤标 `[未执行]`，
  不产生"成功"的假象。
- **只停任务，不杀进程**。`StopTask` 走 `Stop-ScheduledTask -TaskPath -TaskName`，
  不按进程名杀 `defrag.exe`，避免误伤用户自己启动的整理任务。
- **不管理 `Regular Maintenance` 之外的总开关**。本机没有这个任务，代码按 `NotPresent` 处理，
  一旦系统版本里有它就会自动纳入管理。
- **本阶段不动磁盘、不动风扇**。Phase 3 只做维护任务 / 服务 / 注册表三块；
  磁盘脱机在 Phase 2 的 `qmctl disks`，二者在 Phase 4 的 Worker 里串起来。

## 11. RC1 / RC2 变更（2026-11-12）

### 11.1 Critical 的语义修正

`s.Critical = desired`：**原本启用**的任务白天恢复不回来才是关键失败 ——
那是「用户的维护任务被我们关死了」。原本禁用的任务「收回禁用」失败也照样报告，
但不该把整轮判成灾难。

### 11.2 原始启用状态未知（baseline schema 2→3）

夜间装机会在维护任务被夜间闸门禁用的时候拍快照。那一刻观测到的
`enabled=false` 有两种可能：用户本来就没开，或者是我们刚刚关掉的。两者无法区分。

现在这种条目在 baseline 里打 `originalUnknown=true` 并写明原因：

- 白天计划遇到未知项 → **保留 Pending 并说明**，不启用也不禁用；
- 白天验证遇到未知项 → 判「无法确认」，不判通过；
- `qmctl maint baseline-enable/disable <序号> --apply` 视为用户确认，清除未知标记。

新增字段的默认值**不落盘**，所以升级不会让已有基线的完整性校验值失配；
旧 schema（2）按兼容模式读取并告警，只拒绝「比本版本更新」的 schema。

### 11.3 白天恢复失败的重试边界

任务 XML 的白天触发器带**有界**重试（最多 4 小时、每 30 分钟一次）。
白天恢复失败还能再试，但不会一路重试撞进 23:58 的下一个夜间窗口。

### 11.4 权限

`C:/ProgramData/QuietMode` 下的 `config.json` / `state.json` / `baseline.json` /
`run` / `recovery` 由 SYSTEM 读取并据此操作系统对象，普通用户**不应可写**。
部署目录里被 SYSTEM 直接调起的 `qmctl.exe` / `QuietMode.exe` 与脚本树同理。

`QuietMode.Security.psm1` 负责体检与加固：断继承 → 删掉 Users / Authenticated Users /
Everyone 的写类 Allow → 补一条 `Users:ReadAndExecute` → 显式补 `Administrators` 与
`SYSTEM` 的完全控制。`logs` 单独放行追加。判「普通用户」按三个 SID 而不是只按 Users 组，
否则从 `H:\` 继承来的 `Authenticated Users:Modify` 会被漏掉 —— 那也是一条真实的写权限。

早先用的是「给 Users 加 Deny ACE」，两个硬伤已经拆掉：管理员也在 Users 组里（Deny 连
安装器一起挡），而 `state.json` 是原子替换写入，新文件继承父目录 ACL，Deny 当场消失。
安装器现在在**第一个写操作之前**调用 `Unblock-QmLegacyDeny` 清掉残留 Deny —— 否则
连 `recovery\install-<时间戳>` 这个备份目录都建不出来（2026-11-13 21:41 实测崩在这里）。
