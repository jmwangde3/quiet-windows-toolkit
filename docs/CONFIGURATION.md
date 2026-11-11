# 配置与快照（v2 Phase 1）

本文档描述 v2 的配置布局、字段语义、校验规则、迁移路径和快照机制。实现见 `src/QuietMode.Core/Configuration/`。

## 1. 目录布局

机器级（`C:\ProgramData\QuietMode\`，安装时收紧 ACL）：

| 文件 | 职责 |
|---|---|
| `config.json` | 当前机器策略与硬件配置。唯一真源。 |
| `config.backup.json` | 上一次通过校验的配置（last-known-good）。 |
| `state.json` | 运行状态、手动覆盖、调度时间戳、恢复进度。 |
| `baseline.json` | 安装前的原始系统状态快照。 |
| `logs\` | 结构化运行日志。 |
| `recovery\` | 任务 XML 备份、`baseline.previous.json`、恢复所需文件。 |

用户级（`%APPDATA%\QuietMode\`）只放 UI 偏好；v1 的 `settings.ini` / `state.ini` 保留原样作为迁移来源，迁移完成且验收通过后由 Phase 9 清理。

## 2. config.json

```jsonc
{
  "schemaVersion": 2,

  "schedule": {
    "enabled": true,                 // 用户对定时闸门的长期意愿
    "mode": "override",              // override | coexist
    "nightStart": "23:58",
    "dayStart": "10:02",
    "startupOverrideEnabled": true,  // 开机落在夜间窗口时按「开机补跑」判定白天
    "startupDayStart": "06:00",
    "startupGraceMinutes": 15,
    "reconcileIntervalMinutes": 10
  },

  "fan": {
    "enabled": false,                // 安装时由安装器填入真实路径后置 true
    "ipcPath": "",
    "controlExe": "",
    "normalProfile": "",
    "silentProfile": "",
    "flagPath": "",
    "autoSwitch": { "enabled": true, "on": "23:58", "off": "10:02" }
  },

  "disks": {
    "enabled": false,
    "forceBusy": false,
    "targets": [
      {
        "uniqueId": "",              // 首选身份
        "serialNumber": "",          // 备用身份
        "friendlyName": "",
        "sizeBytes": 0,
        "busType": "",
        "enabled": true,
        "legacyNumber": 1            // 仅迁移时写入的线索，不参与匹配
      }
    ]
  },

  "maintenance": {
    "enabled": true,
    "strictNightDisable": true,
    "disableSysMainAtNight": true,
    "maintenanceHour": 10,
    "disableWake": true
  },

  "spinDown": { "enabled": false, "minutes": 20 },

  "startup": { "autoStart": false, "restoreOnBoot": false }
}
```

与规格 §2.2 的差异：额外增加了 `schedule.mode`、`fan.controlExe` / `fan.flagPath` / `fan.autoSwitch`、`startup.*`。这些字段承接 v1 已交付功能（overwrite/coexist 调度、到点自动切风扇、开机自启、开机恢复），删掉会造成功能回退，因此保留；语义与 v1 一致。

`disks.targets[].legacyNumber` 只在 v1 迁移时写入，用于人眼核对日志，**任何匹配逻辑都不得读取它**。

## 3. 校验规则（ConfigValidator）

错误级（`IsValid=false`，调用方必须拒绝执行磁盘操作）：

1. `schemaVersion != 2`。
2. `schedule.mode` 不是 `override` / `coexist`。
3. 时间不是 `H:mm` / `HH:mm`（分钟必须两位，00–59；小时 0–23）。
4. `startupGraceMinutes` 不在 0–120；`reconcileIntervalMinutes` 不在 1–1440；`maintenanceHour` 不在 0–23。
5. `spinDown.minutes` 不在 0–180；`spinDown.enabled=true` 且 `minutes=0`（0 表示永不，语义矛盾）。
6. `fan.enabled=true` 时：`ipcPath` / `controlExe` / `normalProfile` / `silentProfile` 必须是非空绝对路径且文件存在；`normalProfile` 与 `silentProfile` 不得指向同一文件。
7. `disks.enabled=true` 却没有目标；或任一目标既无 `uniqueId` 也无 `serialNumber`；或 `uniqueId` / `serialNumber` 重复。

警告级（不阻断）：`disks.enabled=true` 但所有目标 `enabled=false`、目标缺少 `sizeBytes`、`startupOverrideEnabled=false`、`schedule.enabled=false` 但 `disks.enabled=true`。

**硬规则：无法唯一匹配的磁盘不会被操作，也绝不回退到 Disk 0。** 迁移时解析不出稳定身份的磁盘条目直接丢弃并告警，而不是写一条"占位"目标。

## 4. 原子写入

所有写入走 `AtomicFile.WriteText`：

1. 写 `*.tmp-<pid>-<rand>`；
2. `FileStream.Flush(true)` 落盘；
3. 复读并重新解析 + 重新校验；
4. `File.Replace(tmp, target, backup)` 原子替换，同时把上一版落到 `config.backup.json`；
5. 失败清理临时文件，目标文件保持原状。

加载顺序：`config.json` → 校验 → 失败则 `config.backup.json` → 仍失败则视为"无有效配置"。此时磁盘类操作必须拒绝执行（`ConfigLoadResult.Usable == false`）。

## 5. state.json

```jsonc
{
  "schemaVersion": 2,
  "modeOn": false,
  "schedulePaused": false,
  "autoSilentActive": false,
  "autoSilentSuppressed": false,
  "autoOffDayStamp": "2026-11-11",
  "lastNightRunUtc": "",
  "lastDayRunUtc": "",
  "lastReconcileUtc": "",
  "lastBootReconcileUtc": "",
  "recovery": {
    "active": false,
    "startedUtc": "",
    "reason": "",
    "attempts": 0,
    "steps": [ { "name": "RestoreDisks", "status": "failed", "detail": "磁盘 #1 仍为脱机", "updatedUtc": "" } ]
  }
}
```

`recovery.steps[].status` ∈ `pending | running | ok | failed | skipped`。恢复事务允许重复执行，进度写在这里，进程被杀后可读回继续。

## 6. baseline.json

由 `SnapshotManager.Capture()` 生成，只读采集，不改任何系统状态：

| 段 | 内容 |
|---|---|
| `scheduledTasks` | 10 项（`MaintenanceCatalog` 唯一清单）：6 项维护任务、Regular Maintenance、QueueReporting、两个 v1 闸门任务。每项含 `exists` / `enabled` / `xmlSha256` / `xmlBackup`。 |
| `services` | `SysMain` 的 `startValue` / `startupType` / `status`。 |
| `registry` | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance` 下的 `MaintenanceBoundary`、`WakeUp`（含 `Kind` 与原始值）。 |
| `powerPlan` | 活动方案 GUID、`DISKIDLE` 的 AC/DC 秒数。 |
| `disks` | 每块盘的 `number`（运行时）、`uniqueId`、`serialNumber`、`friendlyName`、`sizeBytes`、`busType`、`isOffline` / `isSystem` / `isBoot`。 |
| `fanControl` | 进程是否运行、控制程序与两个配置文件的 SHA-256、flag 文件状态。 |
| `metadata` | `windowsBuild`、`productName`、`displayVersion`、`createdAtUtc`、`createdBy`、`toolVersion`、`complete`、`integritySha256`。 |

`integritySha256` = 把该字段置空后的紧凑 JSON 的 SHA-256，`SnapshotIntegrity` 计算，读写两侧都可复算。

快照原则：安装时建立；日常切换不覆盖；升级不覆盖；只有用户显式 `--reset` 才重建；重建时上一版落到 `recovery\baseline.previous.json`；恢复失败不删快照。

`complete=false` 表示有受保护任务读不到（非提权时 `\QuietHDD\` 会 `E_ACCESSDENIED`）。此时 `snapshot verify` 仍判 `valid`（结构+哈希正确），但会明确告警，恢复时缺失项按"跳过并记录"处理，不假装成功。

## 7. v1 → v2 迁移

`qmctl config migrate` 读取 `%APPDATA%\QuietMode\settings.ini` + `state.ini`：

| v1 键 | v2 |
|---|---|
| `FanEnabled` / `FanIpcCtl` / `FanControlExe` / `FanNormal` / `FanSilent` / `FanFlag` | `fan.*` |
| `HddEnabled` / `HddForce` / `HddDisks` | `disks.enabled` / `forceBusy` / `targets[]`（编号经 WMI 解析为 `uniqueId`+`serialNumber`） |
| `MaintenanceEnabled` | `maintenance.enabled` |
| `SpinDownEnabled` / `SpinDownMinutes` / `SpinDownSeconds` | `spinDown.*`（无 `SpinDownMinutes` 时按秒/60 折算） |
| `ScheduleMode` / `GateEnabled` | `schedule.mode` / `schedule.enabled` |
| `AutoSilent` / `AutoSilentOn` / `AutoSilentOff` | `fan.autoSwitch.enabled` / `on` / `off`，同时作为 `schedule.nightStart` / `dayStart` |
| `AutoStart` / `RestoreOnBoot` | `startup.*` |
| `state.ini` 全部 | `state.json` 同名字段 |

迁移行为：

- 目标已有 `config.json` 时拒绝，需 `--force`。
- 迁移结果必须通过 ConfigValidator，否则不写盘。
- 含 `<...>` 的占位路径（脱敏残留）清空并告警。
- 磁盘编号解析失败 → 丢弃该条目 + 告警；全部失败 → `disks.enabled=false`，避免"配置说开、实际不敢动"。

## 8. 命令行

```powershell
qmctl [--root PATH] [--quiet] [--json] [--out FILE] <command>
```

| 命令 | 说明 |
|---|---|
| `config show` | 打印 config.json |
| `config validate` | 含文件存在性校验，`INVALID` 退出码 2 |
| `config init [--force]` | 写默认策略 |
| `config migrate [--force]` | v1 → v2 |
| `config path` | 打印路径布局 |
| `snapshot create [--reset]` | 采集 baseline（已存在需 `--reset`） |
| `snapshot verify` | 校验哈希与任务 XML 备份 |
| `snapshot show` | 摘要 |
| `state show` | 打印 state.json |
| `disks list` | 磁盘与稳定身份 |

退出码：`0` 成功、`1` 一般失败、`2` 校验失败。`--quiet` 静默（只留退出码，供 PowerShell 调用）；`--json --out FILE` 把机器可读结果写 UTF-8 文件，绕开控制台代码页（zh-CN 代码页 936 会把 UTF-8 输出解成乱码）。

## 9. PowerShell 层

- `powershell\Modules\QuietMode.Common.psm1`：路径、`qmctl` 调用封装、日志、提权检测。
- `powershell\Modules\QuietMode.Config.psm1`：`Get-QuietModeConfig` / `Get-QuietModeState` / `Test-QuietModeConfig` / `Invoke-QuietModeConfigMigration` / `New-QuietModeBaseline` / `Test-QuietModeBaseline` / `Get-QuietModeDiskTargets`。
- `powershell\Get-QuietModeStatus.ps1`：只读状态汇报。
- `powershell\Test-QuietMode.ps1`：编译 + 单测 + 配置校验 + baseline 校验，非 0 即失败。

脚本层不得再自建磁盘清单或维护任务清单；唯一来源是 `MaintenanceCatalog`。

## 10. Phase 1 边界

已完成：目录、统一配置、校验、迁移、原子写、baseline、恢复进度持久化、版本兼容、CLI、PowerShell 模块、单测。

未做（后续阶段）：真实磁盘联机/脱机（Phase 2）、维护任务统一执行（Phase 3）、调度状态机与 Worker（Phase 6）、托盘拆分（Phase 7）、安装/卸载/迁移收尾（Phase 9）。

Phase 1 的所有代码路径都**不修改运行中的系统状态**：只读 WMI / 注册表 / 任务 XML / 电源方案。
