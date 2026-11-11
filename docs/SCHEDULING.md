# 统一调度（Phase 4）

> 本文描述 v2 的唯一调度控制器：谁做决定、谁执行、状态怎么流转、出问题看哪里。
> 对应规格 §6.1–§6.4 与 §7.3。

## 1. 为什么要有这一层

v1 有两条互不相干的调度链：

| 链 | 触发 | 管什么 | 时间判断 |
|---|---|---|---|
| `gate.ps1`（`QuietHDD_NightGate`） | 23:58 / 10:02 / 开机+2min | 维护任务、SysMain、注册表 | 自己算小时 |
| `diskgate.ps1`（`QuietHDD_DiskGate`） | 同上 | 目标磁盘 Offline/Online | 自己再算一遍 |
| 托盘 `CheckAutoSilent` | 每 30 秒 | 风扇 + 维护 + 磁盘 | 第三套判断 |

三处各自判断时间，于是出现了 2026-11-10 那次「静音关了但硬盘没起来」：`gate.ps1` 恢复任务、`diskgate.ps1` 判成夜间保持脱机、托盘因为 `ModeOn=0` 认为不用管。

v2 把它们合成一条：**一个任务、一个决策点、一个执行器、一次验证**。

```text
\QuietMode\QuietMode-Reconcile   ← 唯一计划任务（SYSTEM / Highest / IgnoreNew / WakeToRun=false）
        │
        ▼
qmctl reconcile --apply --trigger schedule
        │
        ├── StateMachine.Decide()        决策：现在应该是什么状态
        ├── ReconcilePlanner.Build()     顺序：维护 / 磁盘 / 风扇 的固定次序
        ├── Invoke-QuietModeWorker.ps1   执行：只按步骤做事，不做判断
        └── 复观测 + 独立验证            结论：Ok / Degraded / Failed
```

## 2. 触发器

`qmctl task install --apply` 注册的任务（`task print` 可以先看 XML）：

| 触发器 | 配置 |
|---|---|
| 每天 23:58 | `schedule.nightStart`，并带 `Repetition`：每 `reconcileIntervalMinutes`（默认 10）分钟一次，持续 10 小时 |
| 每天 10:02 | `schedule.dayStart` |
| 开机后 2 分钟 | `BootTrigger` + `Delay PT2M` |
| 睡眠恢复 | `EventTrigger`，`Power-Troubleshooter` ID 1，延迟 30 秒 |
| 解锁 | `EventTrigger`，`Security` 4801，延迟 10 秒（需要在系统里开启登录审核，否则不会触发） |
| 登录 | `LogonTrigger`，延迟 45 秒（作为解锁的兜底） |

设置项三条硬要求：**`WakeToRun=false`**（不唤醒机器）、**`MultipleInstancesPolicy=IgnoreNew`**（不并发）、**`ExecutionTimeLimit=PT10M`**（不会挂死）。

夜间那 10 小时的重复校验挂在 23:58 这个触发器上，没有再建第二个任务 —— 多一个任务就多一套调度，这正是要消灭的东西。

## 3. 时间判定

`ScheduleWindow.Evaluate()`，纯函数，三条规则：

1. **完整 HH:mm**，不是只比小时。`23:58 → 10:02` 跨午夜窗口，`InWindow(00:30)` 为真。
2. **开机补偿**：开机不足 `startupGraceMinutes`（15）分钟、当前时间在 `startupDayStart`（06:00）到 `dayStart` 之间 → 按白天处理。`00:00–05:59` 的无人值守重启**不**补偿，保持夜间。
3. **配置非法**（格式错、两个起点相同）→ 按白天处理并写明原因。

补偿是显式配置项（`schedule.startupOverrideEnabled` / `startupDayStart` / `startupGraceMinutes`），不是藏在代码里的魔法值。

## 4. 状态机

`StateMachine.Decide()` 的优先级（规格 §6.4）：

```text
RecoveryPending   ← 恢复事务没做完（最高，禁止再进夜间）
    │
ManualQuiet / ManualNormal ← 用户手动覆盖
    │
SchedulePaused    ← 用户暂停调度 / schedule.enabled=false
    │
EnteringNight / NightActive / NightDegraded / EnteringDay / DayActive ← 自动时间调度
    │
DayActive         ← 默认
```

| 状态 | 含义 | 动作 |
|---|---|---|
| `DayActive` | 白天，无静音 | `none` |
| `EnteringNight` | 夜间窗口到了，还没生效 | `night` |
| `NightActive` | 夜间已生效 | `none`（只验证） |
| `NightDegraded` | 上次夜间有失败项 | `night`（重试） |
| `EnteringDay` | 白天窗口到了，需要恢复 | `day` |
| `ManualQuiet` | 用户手动静音 | `night` |
| `ManualNormal` | 用户手动关掉今晚的自动静音 | 夜间取 `day`，白天 `none` |
| `SchedulePaused` | 调度暂停 | `none` |
| `RecoveryPending` | 恢复未完成 | `restore` |
| `Error` | 决策输入缺失 | 不动作 |

状态写回集中在 `StateMachine.ApplyRun()`，没有任何模块可以自己改 `ModeOn` / `SchedulePaused`。

## 5. 执行顺序

固定在这张表里，`ReconcilePlanner` 是唯一定义处：

| 夜间 | 白天 / restore |
|---|---|
| 1. 维护：停任务 → 禁用 → SysMain → 注册表 | 1. 磁盘 Online（先放盘，才有条件恢复任务） |
| 2. 磁盘 Offline（逐个回读） | 2. 维护：按 baseline 恢复 |
| 3. 风扇切静音配置 | 3. 风扇切常态配置 |
| 4. 全面验证 | 4. 全面验证 |

配置里关掉的模块不会从计划里消失，而是标成 `skipped` 并写明原因 —— 计划始终是四步的形状，人一眼能看出「这一步为什么没做」。

## 6. 成功判定

`ReconcileVerifier` 逐项验证，缺一项就不算成功（规格 §7.4）：

| 组件 | 夜间期望 | 判定方式 |
|---|---|---|
| 维护任务 | 全部现存目标 `Disabled` | 复观测 Task Scheduler |
| SysMain | `Start=4`，未运行 | 复观测服务 |
| `MaintenanceBoundary` | 白天时间 | 复观测注册表 |
| `WakeUp` | `0` | 复观测注册表 |
| 目标磁盘 | 全部 `Offline` | 按稳定身份匹配后读 `MSFT_Disk.IsOffline` |
| 风扇 | `current=` 等于静音配置文件名 | `IpcCtl.exe list` 回读 |

结论三档：

- **Ok** —— 全达成。
- **Degraded** —— 达成但有隐患（任务仍 `Running`、磁盘状态读不到、风扇 IPC 不可用）。
- **Failed** —— 有硬性未达成（任务仍启用、磁盘状态不符、风扇配置不符、注册表没写进去）。

`qmctl reconcile` 的退出码：`0` 成功或无动作，`2` 有失败，`1` 配置不可用。

## 7. 手动控制

```powershell
qmctl state manual-quiet      # 记下手动静音；下一次 reconcile 执行
qmctl state manual-normal     # 记下「今晚别再自动开」；夜间窗口内会执行一次 day
qmctl state pause             # 暂停自动调度（保持当前状态）
qmctl state resume            # 恢复自动调度
qmctl state rearm             # 清除暂停 + 今晚的抑制标记 + 手动标记
qmctl state auto              # 全部交回自动
qmctl reconcile               # 只看决策和计划
qmctl reconcile --apply       # 真的执行
```

`state` 只改 `state.json`，不碰系统；真正的生效统一发生在下一次 `reconcile`。这条分界是为了避免「谁都能改系统」的老问题。

## 8. 与 v1 闸门的关系

`qmctl task status` 会同时检查：

- `\QuietMode\QuietMode-Reconcile` 是否存在；
- `\QuietHDD\QuietHDD_NightGate` 与 `\QuietHDD\QuietHDD_DiskGate` 是否还在启用。

只要有一个 v1 闸门仍启用，就判定为「存在重复调度」。**读不到也算冲突** —— `\QuietHDD\` 的 ACL 让普通用户读不了任务，把「看不见」当成「不存在」正是 v1 那次事故的起点。

停用：`qmctl task disable-legacy --apply`（需要管理员）。

## 9. 已知限制

- **解锁触发依赖审核策略**。`Security` 4801 默认不记录，未开启时该触发器永远不会触发；`LogonTrigger` 只覆盖登录，不覆盖「锁屏后解锁」。这是 Windows 的机制限制，不是实现偷懒。
- **风扇验证依赖 `IpcCtl.exe`**。FanControl 没运行、IPC 无响应时风扇状态记为「未知」，结论降到 `Degraded`，不会假装切成功了。
- **夜间重复校验只在机器醒着时跑**。`WakeToRun=false` 是硬要求，机器睡着就等它醒 —— 醒来时 `Power-Troubleshooter` 事件会补一次。
- **首次安装尚未自动化**。`task install` 只注册任务，旧闸门停用、磁盘身份迁移仍由 Phase 6 的迁移流程负责。
