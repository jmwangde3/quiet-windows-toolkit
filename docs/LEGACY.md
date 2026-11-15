# v1 历史实现（quiet-hdd 脚本集）

> **这一页是历史资料，不是安装指引。** 新用户请回 [`../README.md`](../README.md) 走 v2 流程。
> 本页保留下来，是因为已有 v1 安装的机器需要它作为**迁移来源**和**回滚目标**，
> 以及 `docs/LEGACY_AUDIT.md` / `docs/LEGACY_BEHAVIOR.md` 这批审计资料引用了它。

---

## 1. v1 是什么

v1 是纯 PowerShell 的实现，由一组根目录下的独立脚本组成，每个脚本自己注册计划任务。
它工作过，也解决过真实问题，但有 10 项结构性缺陷，是 v2 重构的直接原因（见第 6 节）。

| 文件 | 作用 |
|---|---|
| `quiet-hdd-offline.ps1` | 设备安装器：生成 `diskgate.ps1` + `diskgate.conf`，注册两个闸门任务，启用/禁用维护闸门，`powercfg` 恢复 |
| `quiet-hdd-night.ps1` | 夜间动作：脱机磁盘、停维护任务、停 `SysMain`、写自动维护边界 |
| `quiet-hdd-daytime.ps1` | 白天动作：联机磁盘、恢复维护任务、恢复 `SysMain` |
| `quiet-hdd-pause.ps1` | `-Pause` / `-Resume` / `-ReArm` / `-Status` |
| `quiet-hdd-setdisks.ps1` | 只改 `diskgate.conf` 的 `Disks=` / `Force=` |
| `quiet-hdd-status.ps1` | 只读状态汇总 |
| `quiet-hdd-fix.ps1` | 应急修复 |
| `enable-utf8-console.ps1` | PS 5.1 控制台 UTF-8 输出（与 v1 无直接关系，仍可用） |

运行时产生的文件：

```
C:\ProgramData\QuietHDD\
  diskgate.ps1            闸门主体（由 offline 脚本生成）
  diskgate.conf           Disks=0,1 / Force=0
  diskgate.log  gate.log  两份日志（两套任务各写一份，这正是问题之一）
%APPDATA%\QuietMode\
  settings.ini            策略配置（INI，无 schema / 无校验 / 非原子写）
  state.ini               状态
  worker_result.txt
```

计划任务（**两个**，都去动同一块盘）：

```
\QuietHDD\QuietHDD_NightGate    23:58 脱机
\QuietHDD\QuietHDD_DiskGate     10:02 联机 / 开机补跑
```

---

## 2. v1 的真实命令（仅在「还没迁移」的机器上有意义）

```powershell
# 安装（管理员）
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1"

# 查看 / 试跑 / 回滚
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1" -Status
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1" -TestNight
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1" -Restore

# 改脱机磁盘编号
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-setdisks.ps1" -Disks '0,1' -Force

# 今晚想用盘
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-pause.ps1" -Pause
powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-pause.ps1" -Resume -ReArm
```

参数全集见 `docs/LEGACY_AUDIT.md` 第 43–46 行。

---

## 3. v1 的行为细节（迁移后仍需要知道的）

- **开机补跑**：开机后 2 分钟的 `AtStartup` 闸门按「开机时长 < 15 分钟则 06:00 起算白天」判定，
  早上开机直接联机；00:00–05:59 的无人值守重启仍保持夜间脱机。v2 用
  `schedule.startupOverrideEnabled` / `startupDayStart` / `startupGraceMinutes` 表达同一语义。
- **DISKIDLE**：v1 把 `DISKIDLE(AC/DC)` 写成 1200 秒，但**白天不还原**，是全局副作用。
  v2 把它做成独立开关（`spinDown.enabled`），进 reconcile 链、写后回读、`false` 时整步跳过。
- **静音开关与硬盘停转**在 v1 里也是两个独立开关，这一点 v2 保留了。

---

## 4. 从 v1 迁移到 v2

不需要手工抄配置。`Install-QuietMode.ps1 -Mode Apply` 会自动执行 `qmctl config migrate`：

1. 读 `%APPDATA%\QuietMode\settings.ini` + `state.ini`；
2. 读 `diskgate.conf` 的 `Disks=`，把每个**磁盘编号**解析成 `uniqueId` + `serialNumber`；
3. 解析不出稳定身份的盘**整条丢弃**并告警 —— 绝不把「猜的盘」写进配置；
4. 写 `C:\ProgramData\QuietMode\config.json`；
5. 注册 v2 任务后停用 `\QuietHDD\` 两个闸门并回读确认；
6. 停不掉就回滚 v2，回到「只有 v1」。

单独跑迁移（只读演练，不写目标）：

```powershell
.\bin\qmctl.exe config migrate
```

> `config migrate` 的 `--force` 会覆盖已存在的 `config.json`。安装器在 `-Force` 时会先备份再删。

**迁移后不要手工删 v1 文件。** 它们要留到 v2 验收通过、连续几夜正常之后。
在那之前，v1 是 `Install-QuietMode.ps1 -Mode Rollback` 的恢复目标。

---

## 5. 回退到 v1

```powershell
# 演练：只打印将做什么
powershell -ExecutionPolicy Bypass -File .\powershell\Install-QuietMode.ps1 -Mode Rollback -DryRun

# 真回滚：删 v2 任务、恢复 v1 闸门启用状态、还原安装前的二进制
powershell -ExecutionPolicy Bypass -File .\powershell\Install-QuietMode.ps1 -Mode Rollback
```

回滚**保留** `config.json` / `state.json` / `baseline.json`，方便复诊 —— 那份数据是唯一能说明
「当时到底发生了什么」的东西。确认不再需要之后再由人手工清理。

卸载（不是回滚）走另一条路：

```powershell
powershell -ExecutionPolicy Bypass -File .\powershell\Uninstall-QuietMode.ps1 -Mode DryRun
powershell -ExecutionPolicy Bypass -File .\powershell\Uninstall-QuietMode.ps1 -Mode Apply
```

卸载按「数据可访问性优先」排序：停调度 → 确认已停 → 磁盘联机 → 维护任务 → `SysMain`/注册表 →
电源 → 风扇 → 托盘，每步回读，幂等可重跑续做。**不删** `baseline` / `recovery` / `logs`。

---

## 6. v1 为什么被重构（10 项结构性缺陷）

1. **两套并行闸门**：`NightGate` 与 `DiskGate` 都在动同一批盘，日志也是两份，
   出问题时无法判断是哪一套干的。
2. **维护任务清单在 6 处重复定义且互不一致**。
3. **白天恢复用硬编码默认值**，而不是安装时抓的原始快照 —— 用户改过的设置会被覆盖。
4. **磁盘身份只用编号**，加一块盘或换个 SATA 口就可能脱错盘。
5. **Worker 只报 `*_REQUESTED` 不回读验证**，「请求成功」被当成「已经成功」。
6. 配置无 schema、无校验、非原子写（INI 直改）。
7. 状态分散在 `settings.ini` + `state.ini` + `worker_result.txt` 三处。
8. 没有快照，无法回答「装之前是什么样」。
9. 脚本各自注册任务，没有单调度不变量，失败时可能只装了一半。
10. 托盘自己算窗口、自己拼提权脚本、直接碰 WMI / 注册表 / 电源 —— 「谁决定」和「谁执行」分裂。

**两项已满足、重构时不得回退**的：系统盘 / 页面文件保护；`DISKIDLE` 与静音开关的独立性。

完整审计见 [`LEGACY_AUDIT.md`](LEGACY_AUDIT.md)，实测行为基线（含 2026-11-10 夜间异常）见
[`LEGACY_BEHAVIOR.md`](LEGACY_BEHAVIOR.md)，重构规格见 [`V2_SPEC.md`](V2_SPEC.md)。

---

## 7. 本机遗留状态（截至 v2.0.5）

- `\QuietHDD\QuietHDD_NightGate` 与 `\QuietHDD\QuietHDD_DiskGate` 仍注册但**已禁用**，由 v2 接管。
- `C:\ProgramData\QuietHDD\diskgate.ps1` 仍在磁盘上，两闸门禁用后不再被执行，可择日清理。
- `%APPDATA%\QuietMode\settings.ini` / `state.ini` 保留作迁移来源，v2 不再读写。
