# SAFETY — 这套工具会动什么、绝不动什么、出问题怎么退

> 面向「准备在自己机器上装」的人。读完这一页你应该能回答三个问题：
> 它会在半夜改我哪些系统状态？哪些东西它永远不碰？它要是搞砸了我怎么退回去？
>
> 规格依据：[`docs/V2_SPEC.md`](docs/V2_SPEC.md) §4.6 禁改范围、§7 失败语义、§8 恢复。

---

## 一、写面清单（会改动的全部东西）

只有下面这些。不在表里的系统状态，它一个字节都不写。

| # | 对象 | 夜间（23:58） | 白天（10:02） | 依据 |
|---|---|---|---|---|
| 1 | 维护任务 `\Microsoft\Windows\Defrag\ScheduledDefrag` | Stop → Disable | 按 baseline 恢复 | `MaintenanceCatalog` |
| 2 | `\Microsoft\Windows\DiskCleanup\SilentCleanup` | Stop → Disable | 按 baseline 恢复 | 同上 |
| 3 | `\Microsoft\Windows\Data Integrity Scan\Data Integrity Check And Scan` | Stop → Disable | 按 baseline 恢复 | 同上 |
| 4 | `\Microsoft\Windows\Data Integrity Scan\Data Integrity Scan` | Stop → Disable | 按 baseline 恢复 | 同上 |
| 5 | `\Microsoft\Windows\Data Integrity Scan\Data Integrity Scan for Crash Recovery` | Stop → Disable | 按 baseline 恢复 | v1 清单漏掉的第 8 项 |
| 6 | `\Microsoft\Windows\Chkdsk\ProactiveScan` | Stop → Disable | 按 baseline 恢复 | 同上 |
| 7 | `\Microsoft\Windows\Shell\IndexerAutomaticMaintenance` | Stop → Disable | 按 baseline 恢复 | 同上 |
| 8 | `\Microsoft\Windows\TaskScheduler\Regular Maintenance` | 本机不存在 → 跳过 | 跳过 | baseline 记 `exists=false` |
| 9 | 服务 `SysMain` | `Start=4`（Disabled）+ 停止 | 按 baseline 恢复启动类型与运行状态 | `maintenance.disableSysMainAtNight` |
| 10 | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance\MaintenanceBoundary` | `10` | 按 baseline 恢复 | `maintenance.maintenanceHour=10` |
| 11 | 同键 `WakeUp` | `0` | 按 baseline 恢复 | `maintenance.disableWake=true` |
| 12 | 电源设置 `SUB_DISK DISKIDLE`（AC + DC） | 20 分钟 = 1200 秒 | 同值（夜间不额外改） | `spinDown.enabled=true` |
| 13 | 磁盘（按 `uniqueId` 匹配，不按磁盘编号） | `Set-Disk -IsOffline $true` | `IsOffline $false` | `disks.targets` |
| 14 | FanControl 当前配置（命名管道切换，不重启进程） | 静音配置 | 常态配置 | `fan.silentProfile` / `fan.normalProfile` |
| 15 | 计划任务 `\QuietMode\QuietMode-Reconcile` | 仅安装 / 卸载时注册或删除 | — | `SchedulerInstaller` |
| 16 | 任务文件夹与任务对象的安全描述符 | 仅安装时补一条 `Users` 读 ACE（托盘免提权读状态） | — | `Grant-TaskRead.ps1` |

第 13 项是本机实测配置（两块 14TB / 4TB 机械盘）。换机器时 `disks.targets` 必须改成你自己的 `uniqueId`；安装器在 `matched != true` 或 `uniqueId` 为空时**直接失败退出**，不会「猜一个盘脱机」。

第 14 项只切配置，不杀进程、不改 FanControl 自己的曲线文件。

---

## 二、绝不动的东西

### 2.1 任务路径黑名单（写死在代码里，配置也改不掉）

```
\Microsoft\Windows\WindowsUpdate\
\Microsoft\Windows\Defender\
\Microsoft\Windows\Windows Defender\
\Microsoft\Windows\Firewall\
\Microsoft\Windows\CertificateServicesClient\
\Microsoft\Windows\BitLocker\
```

`MaintenanceCatalog.SelfCheck()` 在启动时自检：只要清单里有人把条目加进这些前缀，**planner 直接拒绝整条计划**，而不是「照做然后等出事」。Windows Update / Defender / 防火墙 / 证书 / BitLocker 永远不会因为「夜间静音」被停用。

### 2.2 不参与夜间停用的任务

`QueueReporting`（Windows 错误报告队列）标记为 `nightDisable=false`。它是 `crash-recovery` 模块的条目，**默认不进入夜间停用清单**——半夜崩溃报告照常上报。`maint status` 里它显示 `enabled=true` 是正确状态，不是漏了。

### 2.3 磁盘硬阻断（`DiskSafetyGuard`）

以下情形**直接拒绝执行**，没有任何配置项可以绕过：

| 阻断原因 | 说明 |
|---|---|
| 系统盘 / 启动盘 | 盘上就是正在跑的 Windows |
| 页面文件所在盘 | 脱机会让系统假死 |
| 存储池成员 | 脱机会破坏池 |
| 状态未知 / 读不到 | 看不见不等于安全 |
| 无稳定身份（`uniqueId` 与序列号都缺失） | 拒绝对「编号」动手，编号会漂 |
| 多块盘匹配到同一身份 | 拒绝猜测 |

`--force` 只能越过**一项**：`BUSY`（有进程句柄占用）。越过时会在日志里写明「有 N 个进程的可执行文件或命令行指向该盘」——本机实测 Disk1 上常驻 15 个进程，这是既有事实，不是新引入的风险，但你要知道夜间脱机时它们会失去对该盘的数据访问。

### 2.4 其它永不做的事

- 不写 `HKLM\SYSTEM\CurrentControlSet\Services\` 下除 `SysMain\Start` 以外的任何值
- 不改任何安全策略、组策略、防火墙规则、Defender 排除项
- 不装驱动、不注册服务、不改引导项
- 不删用户文件；`recovery\` 与 `logs\` 在卸载时**保留**
- 不联网（唯一的网络访问是 `dotnet build` 还原 NuGet 包，运行期零外连），无遥测

---

## 三、失败语义：它不会「假装成功」

这三条是整个设计的底线，写进了每个写路径。

**① 默认演练。** 所有会改系统的命令默认只打印计划，`--apply` 才落盘。`Install-QuietMode.ps1` 默认 `-Mode DryRun`；`Uninstall-QuietMode.ps1` 默认 `-Mode DryRun`；`Install-QuietMode.ps1 -Mode Rollback` 也可加 `-DryRun` 预演。

**② 每次写后回读。** 写 `MaintenanceBoundary` 后重新读；`Set-Disk` 后回读 `IsOffline`；`powercfg` 后独立回读 AC/DC；改任务状态后 `GetTasks` 重新枚举。C# 侧还会**再独立观测一次**才下结论。回读不符 → `Failed`（未达成）或 `Degraded`（部分达成），退出码 2。

**③ 未提权绝不做一半。** Worker 收到请求但当前不是管理员时，所有需要提权的步骤标 `notrun`，整体退出码 2。不存在「维护任务禁用了、磁盘还联机」这种半截状态。

**④ Worker 不做判断。** `Invoke-QuietModeWorker.ps1` 只按请求 JSON 里给的步骤做事，不自己算窗口、不自己决定要不要跑。决策只有 `StateMachine` 一处，执行只有 Worker 一处。

---

## 四、三条不变量

### 4.1 任何时刻只允许一套调度器

v1（`\QuietHDD\` 两个闸门）与 v2（`\QuietMode\QuietMode-Reconcile`）都能让磁盘脱机。两套同时启用会互相打架——这正是 v1 时代「早上 10:02 盘没联机」的根因之一。

所以安装器是**顺序化的、带退路的**：

```
v2 注册失败        → 完全不动 v1（回到安装前）
v2 注册成功、v1 停不掉 → 回滚 v2（回到「只有 v1」）
收尾再复核一次      → 仍有并行调度就报 FAIL
```

`qmctl task status` 的退出码就是这个不变量的探针：`0` = 单调度器，`2` = 有冲突或读不到。

### 4.2 看不见 ≠ 不存在

读不到任务状态、读不到 `IsOffline`、读不到注册表值时，一律**按最坏情况处理**：报「无法确认」并当冲突/未达成，绝不渲染成「正常」。

托盘同理：`offline` / `exists` 读不到时序列化为 `null`，界面显示「状态未知」。

### 4.3 没有回读确认的写入，不算成功

见上一节 ③。

---

## 五、退路（按代价从低到高）

### 5.0 全都在这一个目录里

```
C:\ProgramData\QuietMode\recovery\
  install-<时间戳>\     每次安装前的二进制、config/state/baseline、v1 三份配置、v1 任务 XML
  baseline-<时间戳>\    baseline 重建前的备份（含 SHA256）
  tasks\                任务 XML 导出
  task-visibility-<时间戳>\  任务 ACL 改动前的 SDDL
```

安装器在**任何写入之前**先建备份目录并写完 `migration-before.txt`。卸载器**不删** `recovery\`、`logs\`、`baseline.json` —— 那是出事后唯一的复诊材料。

### 5.1 只是「今晚不想静音」

```powershell
qmctl state manual-normal          # 立刻恢复白天态，今晚不再自动静音
qmctl state rearm                  # 恢复自动调度
```

或托盘右键「取消静音」。手动静音会写 `modeOnSinceUtc`；跨关机后若发现是**开机前**按下的，会被当作过期状态自动清掉并写 Notes——不会出现「中午开机又跑一遍夜链」。

### 5.2 状态不对，先修不重装

```powershell
.\powershell\Repair-QuietMode.ps1            # 只诊断
.\powershell\Repair-QuietMode.ps1 -Apply     # 只做三件低风险修复：重注册任务 / 对齐一次 / 加固权限
```

`Repair` **不动磁盘、不动维护任务、不重建 baseline**。它修的是「调度没跑」，不是「策略错了」。

### 5.3 换个起点重来

```powershell
.\powershell\Reset-Baseline.ps1              # 夜间会拒绝执行（相位闸门）
.\powershell\Reset-Baseline.ps1 -Force       # 明知在夜间也要重建
```

重建前自动备份旧 baseline 并算 SHA256，重建后三重回读（schema / 完整性 / 条目数）。baseline 是白天恢复的唯一依据，乱重建的后果是「恢复成错误的值」，所以默认拦。

### 5.4 完整卸载（保留数据）

```powershell
.\powershell\Uninstall-QuietMode.ps1                    # 演练，只打印计划
.\powershell\Uninstall-QuietMode.ps1 -Mode Apply        # 真做
```

顺序固定：**停调度 → 确认已停 → 磁盘联机 → 维护任务 → SysMain/注册表 → 电源 → 风扇 → 托盘**。数据可访问性排第一位：先让盘回来，再谈别的。

可裁剪：

| 参数 | 用途 |
|---|---|
| `-Only Disks` | 只恢复磁盘联机 |
| `-Only Disks,Maintenance` | 只做磁盘与维护 |
| `-KeepTask` | 保留调度任务，只恢复系统（方便「先停一夜，明天再看」） |
| `-KeepTray` | 不结束托盘进程 |

幂等，可以中断后重跑续做。

### 5.5 回滚到安装前

```powershell
.\powershell\Install-QuietMode.ps1 -Mode Rollback -DryRun    # 预演
.\powershell\Install-QuietMode.ps1 -Mode Rollback            # 真回滚（需管理员）
```

做三件事：删 v2 任务 → 启用 v1 闸门 → 从最近一次 `install-*` 备份还原旧二进制。`config.json` / `baseline.json` **保留**，方便复诊后再决定是否清。

备份还原走**白名单**（只认六个二进制文件名），目录里那几个 `.txt` / `.json` 不会被顺手复制进部署目录。

> 回滚后 v1 闸门是**启用**状态。如果就这么过夜，v1 那套旧脚本会接管（盘照样脱机，但走的是没人维护的路径）。真回滚请安排在没有夜间任务的时段，做完立刻装回。

### 5.6 全手动兜底（不依赖本工具任何代码）

```powershell
# 磁盘联机
Get-Disk | Where-Object { $_.IsOffline } | Set-Disk -IsOffline $false

# SysMain 恢复
Set-Service SysMain -StartupType Automatic -Status Running

# 维护任务恢复（按你自己记得的原始状态）
Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' | Enable-ScheduledTask

# DISKIDLE 恢复
powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE 1200
powercfg /setdcvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE 1200
powercfg /setactive SCHEME_CURRENT

# 删掉 v2 任务
Unregister-ScheduledTask -TaskName 'QuietMode-Reconcile' -TaskPath '\QuietMode\' -Confirm:$false
```

---

## 六、已知风险（诚实清单）

| 风险 | 现状 | 说明 |
|---|---|---|
| 夜间脱机时盘上进程失去访问 | 已知，会告警 | 本机 Disk1 常驻 15 个进程；脱机后它们读写会报错。安装前用 `qmctl disks inspect` 看一遍 |
| `Regular Maintenance` 在 26200 上不存在 | 已处理 | baseline 记 `exists=false`，不参与验证，不会误禁/误恢复 |
| 睡眠恢复触发依赖事件 ID 4801 | 部分验证 | 任务 XML 里有 `Power-Troubleshooter/4801` 触发器，真实睡眠恢复未逐次实测 |
| 开机触发器只验证了 XML | 部分验证 | `BootTrigger` 在 XML 里，真实重启后补跑未逐次实测 |
| 跨关机的手动静音 | 已修（rc.3） | `modeOnSinceUtc` + 开机 10 分钟窗口判定 |
| 手动静音下每 10 分钟重跑整条夜链 | 已修（rc.3） | 决策对齐自动夜间，已生效后 `action=none` |
| `state.json` 的 `controllerState` 可能停在 `EnteringNight` | 已修，待部署 | 已提交，需下次 Apply 才进部署树；`reconcile.log` 与 `qmctl status` 始终是准的 |

---

## 七、装之前请自己确认

1. `qmctl disks list` 能看到你的目标盘，且 `uniqueId` 非空。
2. `qmctl disks inspect all` 里目标盘**没有**页面文件、不是存储池成员。
3. `qmctl config validate` 返回 `VALID`。
4. `Install-QuietMode.ps1` 不带 `-Mode Apply` 先跑一遍，七段预检全绿。
5. 备份目录 `C:\ProgramData\QuietMode\recovery\` 所在盘**不在**你的脱机目标里。
6. FanControl 的两个配置文件名与 `config.json` 里 `fan.normalProfile` / `fan.silentProfile` 一致。

前五条任一不满足，装上去就是给未来的自己挖坑。
