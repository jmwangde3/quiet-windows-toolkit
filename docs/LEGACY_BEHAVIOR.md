# LEGACY BEHAVIOR — v1 行为基线（Phase 0）

> 本文记录 v1 **实际可观测行为**，作为 v2 重构不得无意改变的对照基线。
> 证据来源：本机 `diskgate.log` / `gate.log` / `QuietMode.log` / `worker_result.txt` / `settings.ini` / `state.ini` / `diskgate.conf`，截至 2026-11-11 02:00。
> 本文已脱敏：账号、工作区、软件路径、磁盘序列号、PID 均以占位符表示。

---

## 1. 时间模型

| 项 | v1 行为 |
|---|---|
| 夜间窗口 | `23:58 <= NIGHT < 10:02`，跨午夜 |
| 判定方式 | **`Get-Date.Hour`**（只看小时），不是完整 `HH:mm` |
| 夜间任务 | Daily 23:58 |
| 白天任务 | Daily 10:02 |
| 开机补跑 | AtStartup + `PT2M`；`diskgate.ps1` 内用 `Win32_OperatingSystem.LastBootUpTime` 判开机 <15 分钟时把「白天起始」从 10 点提前到 06 点 |
| 无人值守重启 | 00:00–05:59 仍判 NIGHT，保持脱机 |
| 自动静音窗口 | 托盘定时器每 60 s 检查，边界后 45 分钟窗口内动手，窗口外一律不动 |
| 周期校验 | **无**（只有 Daily 两个触发点 + AtStartup） |
| 睡眠恢复 / 解锁补偿 | **无** |

`quiet-hdd-night.ps1` 的 `-MaintenanceHour` 默认 **14**，而 `quiet-hdd-daytime.ps1` 写死 **10**，方案要求的默认白天维护时间是 10 —— v1 两个入口不一致。

---

## 2. 夜间路径（v1 实际执行）

三条独立路径，无仲裁：

**A. `\QuietHDD\QuietHDD_NightGate` → `gate.ps1`（SYSTEM）**
```
if ($night) {
  foreach 6 项维护任务: Stop-ScheduledTask; Disable-ScheduledTask
  Get-Process defrag | Stop-Process -Force
  log: OFF:<name> ... KILL:defrag
}
```
- 不重新查询任务状态，不验证是否真的 Disabled。
- 不管理 Regular Maintenance、Crash Recovery、SysMain、注册表。
- 证据：`gate.log` 每次夜间均含 `KILL:defrag`。

**B. `\QuietHDD\QuietHDD_DiskGate` → `diskgate.ps1`（SYSTEM）**
```
读 diskgate.conf (Disks=0,1 / Force=0)
按小时 + 开机补跑判定 NIGHT/DAY
NIGHT:
  跳过 IsBoot / IsSystem / 含页面文件的盘
  扫描 Win32_Process 的 ExecutablePath/CommandLine 是否落在目标盘符
  有占用且 Force=0 -> SKIP_BUSY:DiskN:pids=...  （整盘跳过）
  否则 Set-Disk -Number N -IsOffline $true
  记 OFFLINE:DiskN / ALREADY_OFF:DiskN
```
- 完成后**有** `Get-Disk` 回读（日志 `ONLINE:Disk0:letters=J:` 即来自重新枚举分区）。
- 不报告「夜间不完整」这一状态，日志之外无消费者。

**C. 托盘 `AutoSilent`（用户态，定时器）**
```
23:58 窗口内 -> IpcCtl load <silent profile>
设置 AutoSilentActive = true
菜单显示「静音模式：开启（自动·夜间风扇）」
```
- **只切风扇**，不碰硬盘、维护任务、SysMain、注册表、闸门。
- 用户点「关闭静音模式」/`StopAutoSilent` → 切回常态 + 置 `AutoSilentSuppressed`，**并触发一次完整 `Apply(false)`**（见第 4 节，这是当前最大问题）。

**旧脚本 `quiet-hdd-night.ps1`（手动运行时才生效，非调度）**
- 7 项维护任务（含 Crash Recovery）+ Regular Maintenance 禁用
- SysMain 停用并设 Disabled
- `MaintenanceBoundary=<MaintenanceHour>`（默认 14）、`WakeUp=0`
- `powercfg /setacvalueindex ... DISKIDLE 0`
- `-KeepDefrag` / `-KeepSysMain` 可跳过禁用，会弱化夜间策略

---

## 3. 白天路径（v1 实际执行）

**A. `gate.ps1`（10:02）**：6 项维护任务 `Enable-ScheduledTask`，无回读验证。

**B. `diskgate.ps1`（10:02 / 开机补跑）**：目标盘 `Set-Disk -IsOffline $false`，回读分区盘符记 `ONLINE:DiskN:letters=...`。

**C. 托盘 `CheckAutoSilent` 日间窗口（10:02–10:47）**：若 `ModeOn=1` 且当天未做过，触发一次完整 `Apply(false)`（风扇常态 + 联机 + 6 项维护任务 ON + 闸门 RESUMED），用 `AutoOffDayStamp` 去重。

**D. `BootReconcile`（开机）**：按**实际磁盘 WMI 状态**判定
- `ModeOn=1` → 3 s 后完整 `Apply(false)`
- `ModeOn=0` 但有盘脱机 → 等 150 s 让 AtStartup 闸门先跑，复查仍脱机才提权 `Apply(false)`
- 23:58 起 45 分钟夜间窗口内跳过
- UAC 取消 / Worker 超时 / 无结果时**保持旧状态**，不假装成功，下次开机可重试

**E. 旧 `quiet-hdd-daytime.ps1`（手动）**：启用 6 项维护任务、**删除** MaintenanceBoundary / WakeUp 两个注册表值、SysMain 恢复 `Automatic` 并启动、再写入 MaintenanceBoundary=10 / WakeUp=0。

> 关键：白天恢复用的是**硬编码默认值 + 删除值**，不是安装前快照。方案 §4.3 / §13.11 要求改为按原始存在状态与数据恢复。

---

## 4. 2026-11-10 夜间实测异常（必须保留为基线证据）

```
[23:58:03] diskgate.log  mode=NIGHT OFFLINE:Disk0 SKIP_BUSY:Disk1:pids=<8 个 PID>
[23:58:04] gate.log      mode=NIGHT OFF:ScheduledDefrag ... OFF:IndexerAutomaticMaintenance KILL:defrag
[23:58:36] QuietMode.log AutoSilent: now=23:58 want=silent
                          SetFanSilent -> FanControl_Silent_Lite.json  OK  user=<USER>
[23:58:48] QuietMode.log === StopAutoSilent (user) ===
                          SetFanSilent -> FanControl.json  OK
                          Refresh: fan=FanControl.json spin=300s
[23:58:58] QuietMode.log === Apply OFF ===
[23:59:01] worker_result.txt:
             HDD_CONF=Disks=0,1
             HDD=ONLINE_REQUESTED
             MT=ON:ScheduledDefrag / SilentCleanup / Data Integrity Check And Scan /
                Data Integrity Scan / ProactiveScan / IndexerAutomaticMaintenance
             GATE=RESUMED:QuietHDD_NightGate
             GATE=RESUMED:QuietHDD_DiskGate
[23:59:00] diskgate.log  mode=DAY ONLINE:Disk0:letters=J: OK_ONLINE:Disk1
```

结论：

1. 夜间策略建立后 **55 秒**被一次用户「退出自动静音」连带执行完整白天恢复，全部撤销。
2. `HDD=ONLINE_REQUESTED` 只代表调用过 `-ForceDay`，托盘不回读验证；方案 §3.4 / §7.4 要求禁止。
3. `SKIP_BUSY:Disk1` 使 4 TB 盘整夜未脱机，且无自动重试、无降级状态上报。
4. 两个闸门被 `GATE=RESUMED` 重新启用，与「夜间静音接管」的语义冲突。
5. `Refresh: spin=300s` 与注册表 `ACSettingIndex=1200` 不一致，DISKIDLE 读数链路存在可疑分叉（待 Phase 1 核实）。

---

## 5. 状态与覆盖语义（v1）

| 状态字段 | 位置 | 含义 |
|---|---|---|
| `ModeOn` | `state.ini` | 手动/整体静音模式 |
| `SchedulePaused` | `state.ini` | 定时闸门被静音接管 |
| `AutoSilentActive` | `state.ini` | 自动静音当前生效中 |
| `AutoSilentSuppressed` | `state.ini` | 本时段被手动退出，今晚不再自动切回 |
| `AutoOffDayStamp` | `state.ini` | 当日已做过一次完整关闭，防重复 |
| `GateEnabled` | `settings.ini`（**当前文件未持久化该键，走默认值 true**） | 计划闸门长期开关 |
| `ScheduleMode` | `settings.ini` | `override` / `coexist` |

优先级（v1 事实，非文档声明）：安全恢复 > 用户手动 > 自动静音 > 定时闸门 > 默认白天。

**方案 §6.3 要求把这些映射成集中式状态机**（`DayActive` / `EnteringNight` / `NightActive` / `NightDegraded` / `EnteringDay` / `ManualQuiet` / `ManualNormal` / `SchedulePaused` / `RecoveryPending` / `Error`），v1 目前是分散布尔字段，且 `StopAutoSilent` 越级改整体状态。

---

## 6. Pause / Resume / ReArm（`quiet-hdd-pause.ps1`）

| 命令 | 行为 |
|---|---|
| `-Pause` | Disable `\QuietHDD\QuietHDD_DiskGate` + 强制联机两块盘 |
| `-Resume` | 仅 Enable 任务，**不动盘** |
| `-Resume -ReArm` | Enable 后按当前时钟立刻跑一次闸门 |
| `-Status` | 报告任务 State 与磁盘状态 |

托盘「计划闸门」开关走 `%APPDATA%\QuietMode\gate.ps1`（提权）：Enable/Disable 两个 `\QuietHDD\` 任务，**关闭闸门时顺带把 `Disks=0,1` 联机**，回头读 `State` 校验。

---

## 7. 硬盘空闲停转（已符合方案 §8，必须保留）

| 项 | v1 行为 |
|---|---|
| 独立性 | 与静音模式**完全解耦**；`Apply()` 不碰 DISKIDLE |
| 默认值 | `SpinDownEnabled=false`（C# 默认），实际系统值由注册表回读决定 |
| 单位 | 分钟（0–180，步进 5），`SpinDownSeconds = Minutes*60`，兼容旧 `SpinDownSeconds` 键 |
| 写入 | 提权脚本 `powercfg /setacvalueindex` + `/setdcvalueindex` + `/s`，AC/DC 同写 |
| 校验 | 写后读 `HKLM\...\PowerSchemes\<active>\SUB_DISK\DISKIDLE\ACSettingIndex` |
| 菜单 | 勾选标记取自系统实测值，不是配置值；「开启 · 空闲 20 分钟后停转」 |
| 关闭 | 写 0（永不停转），不是删键 |
| 当前实测 | ActiveScheme `<GUID>`，AC=1200 s，DC=1200 s |

重构时**不得**把 DISKIDLE 与整盘脱机合并成同一开关，也不得让开关静音模式改动它。

---

## 8. 状态显示判定（v1 缺口）

托盘图标与菜单只依据：
- 风扇：读 `IpcCtl list` 的 `current=` 与静音配置比对
- 硬盘：`MSFT_Disk.IsOffline`（WMI，非提权可读）
- DISKIDLE：注册表读 AC 值

**不验证**：维护任务实际 Enabled 状态、Regular Maintenance、SysMain、MaintenanceBoundary、WakeUp、闸门任务 State、Worker 结果的实状态回读。

因此方案 §7.4 要求的「所有组件验证成功才显示『静音已完全生效』」在 v1 不成立；且 §0.1「不允许在状态未知时显示静音成功」当前靠 `fanUnknown` 与 `busy` 部分兜底，覆盖不全。

---

## 9. 必须保留的行为（重构红线）

1. 默认窗口 23:58 → 10:02，自动维护 10:00，WakeUp=0。
2. 夜间必须停止并禁用指定维护任务，不能只改维护时间。
3. 夜间闸门不得删除。
4. 默认不开启 SysMain。
5. 机械硬盘整盘脱机功能不得取消。
6. DISKIDLE 空闲停转与整盘脱机是两个独立开关。
7. 状态未知不得显示静音成功。
8. 系统盘 / 启动盘 / 页面文件盘保护必须保持。
9. 开机补跑规则（开机 <15 分钟、06:00 起算白天、00:00–05:59 保持夜间）保持不变，除非有明确测试与决策。
10. Pause / Resume / ReArm / override / coexist 语义保持不变，映射到新状态机。
11. 托盘双击切换、右键菜单 12 项、开机自启、风扇/磁盘/闸门/停转查看与设置等使用习惯保持不变。

---

## 10. 已知异常与待核项

| 编号 | 现象 | 影响 |
|---|---|---|
| B01 | 夜间 `StopAutoSilent` 连带完整 `Apply(false)` | 夜间策略被撤销（已实测） |
| B02 | `SKIP_BUSY:Disk1` 多进程占用，整盘跳过且无重试 | 夜间静音不完整 |
| B03 | 维护清单 6 处不一致，Crash Recovery / Regular Maintenance 白天无恢复 | 恢复不完整 |
| B04 | 白天删除注册表值 + SysMain 硬设 Automatic | 破坏用户原始配置 |
| B05 | `gate.ps1` 无条件 `Stop-Process -Force defrag` | 违反方案 §4.2 |
| B06 | `HDD=*_REQUESTED` 未回读验证 | 状态显示可能不实 |
| B07 | `Refresh: spin=300s` 与注册表 1200 s 不一致 | 读数链路待核 |
| B08 | `\QuietHDD\` 任务实时 State 非管理员不可读 | 诊断受限 |
| B09 | 两套闸门 + 旧脚本 + 托盘定时器无仲裁 | 竞态 |
| B10 | 配置 INI 无校验 / 非原子写 / 无 last-known-good | 配置损坏即失控 |

Phase 1 起逐项关闭，关闭顺序按方案 §11 的阶段计划执行。
