# 托盘（Phase 5）

托盘不再是一个"什么都干"的单文件程序。它现在只做三件事：

1. 把 `qmctl status` 报回来的**事实**画到菜单和图标上；
2. 把用户的点击转成**一次** `qmctl` 调用；
3. 把调用结果（包括失败原因）如实弹出来。

它**不**计算"现在该不该静音"，**不**读写系统设置，**不**自己写计划任务或电源方案。
那两件事的唯一主人是：

```
\QuietMode\QuietMode-Reconcile  →  qmctl reconcile  →  Invoke-QuietModeWorker.ps1
```

---

## 1. 目录结构

```
QuietMode/
  Program.cs                 入口：单实例、--diag / --status / --selftest
  App/
    AppPaths.cs              程序目录、日志、qmctl 定位、tray.ini
    AutoStart.cs             HKCU\...\Run 自启 + Win11 托盘常显（shell 职责）
    TrayLog.cs               追加式日志（1 MB 轮转）
  Core/
    QmctlClient.cs           唯一的 qmctl 调用通道（含 UAC 提权）
    MiniJson.cs              只读 JSON 解析（托盘不引用 Core.dll）
  Ui/
    TrayApp.cs               菜单 / 定时刷新 / 动作编排
    SettingsForm.cs          config.json 的表单视图（逐项 config set）
    Icons.cs / Prompt.cs     图标与数字输入框
  Diag/
    Diagnostics.cs           --diag 自检输出
```

编译产物仍是**单个** `QuietMode.exe`（net481 / WinForms），部署时与 `qmctl.exe` 放在同一目录。
托盘刻意不引用 `QuietMode.Core.dll` / `QuietMode.Windows.dll`：跨进程只走 JSON，
这样"谁决定、谁执行、谁显示"三层不会因为共享内存对象而耦合。

---

## 2. 与 qmctl 的契约

托盘只认下面四条命令的 JSON，不解析人类可读输出（避开代码页 936）。

### 2.1 `qmctl status --json`

一次调用给出渲染所需的全部事实：

| 节点 | 关键字段 | 托盘用途 |
|---|---|---|
| `config` | `exists` / `ok` / `error` / `warnings` | 配置坏了显示"不可用"，并**跳过**自启对账 |
| `schedule` | `enabled` / `mode` / `nightStart` / `dayStart` | 菜单"计划闸门"一行 |
| `startup` | `autoStart` / `restoreOnBoot` | 启动时与注册表对账 |
| `window` | `isNight` / `nowText` / `reason` | 说明"为什么现在是这个状态" |
| `controller` | `state` / `text` / `action` / `reason` | 第一行状态、主开关文案 |
| `state` | `modeOn` / `autoSilentActive` / `suppressed` / `lastVerdict` / `consecutiveFailures` | 图标颜色、失败标记 |
| `fan` | `enabled` / `available` / `currentProfile` / `match` | 风扇一行；`match=unknown` 即"读不到" |
| `disks.targets[]` | `number` / `matched` / `offline`(可 null) / `letters` | 磁盘一行；`offline=null` 显示"状态未知" |
| `spinDown` | `acSeconds` / `dcSeconds` / `configuredMinutes` | 停转一行 |
| `task` | `exists` / `state` / `conflict` / `error` | 未注册或 v1 闸门仍在时的**显式警告** |

约定：

- `offline` / `exists` 这类**可空布尔**在"读不到"时必须是 `null`，不能是 `false`。
  托盘据此显示"未知"，而不是把看不见当成正常。
- `status` 在配置不可用时退出码为 2，但**仍然输出完整 JSON**（用默认策略撑住排版），
  托盘据 `config.ok` 决定是否禁用操作。

### 2.2 `qmctl apply <intent> --apply`

一个用户动作 = 一次进程 = 一次 UAC。`intent` 取值：
`manual-quiet` / `manual-normal` / `auto` / `pause` / `resume` / `rearm`。

它先写 `state.json`（意图），再调用 `reconcile`（执行 + 验证）。
意图与结果分离：写意图失败就**不执行**，执行失败就保留意图等下次重试。

### 2.3 `qmctl config set <key> <value>`

设置窗口用它逐项写入，写入口仍然是 `ConfigStore`（原子写 + 校验 + 复读）。
托盘先按普通权限试一次，被 ACL 拒绝再提权重试一次；用户取消 UAC 就报取消，不假装成功。

### 2.4 `qmctl spin set <分钟> --apply`

DISKIDLE 是**独立开关**，与静音模式解耦。写入后回读 `powercfg /query`，
回读值与目标不一致时返回 `VERIFY_MISMATCH`，托盘照实显示"未生效"。

---

## 3. 托盘不再做的事（对比 v1）

| v1 行为 | v2 |
|---|---|
| `Apply(on, silent)` 自己拼 Worker 脚本、自己 `RunAs` | 删除；改调 `qmctl apply` |
| `CheckAutoSilent()` 每 60 秒算窗口、切风扇、写抑制标记 | 删除；调度与抑制都在 `StateMachine`，托盘只显示 |
| `BootReconcile()` 自己判断开机该不该恢复 | 删除；交给计划任务的开机触发（`trigger=boot`） |
| `Sys.BuildGateScript()` / `FixDiskGateScript()` | 删除；v1 闸门由 `qmctl task disable-legacy --apply` 停用 |
| `settings.ini` + `state.ini` 双份状态 | `config.json` + `state.json`，由 qmctl 独占写入 |
| 自己调 WMI / IpcCtl / 注册表 | 只调 `qmctl status` |

**唯一保留的 shell 职责**：`HKCU\...\Run` 自启项、Win11 托盘图标常显（`IsPromoted`）、
打开日志 / 任务栏设置。这些不是"业务策略"，也不需要管理员。

---

## 4. 图标语义

| 图标 | 含义 | 判定 |
|---|---|---|
| 绿色月亮 | 静音已生效 | 目标盘全脱机 + 风扇静音档 + 无失败 |
| 蓝色风扇 | 白天正常 | 目标盘全在线 + 风扇常态 |
| 琥珀感叹 | 只做到一半 | 风扇或磁盘与决策不符，或有盘状态未知 |
| 红色感叹 | 出错 | 配置不可用 / `lastVerdict=Failed` / qmctl 未找到 |

判定只用观测值，不用"我们刚执行过所以应该成功了"。

---

## 5. 失败语义

| 情况 | 界面 | 系统 |
|---|---|---|
| 找不到 `qmctl.exe` | 菜单全部禁用 + 气泡说明路径 | 不动 |
| `config.json` 缺失/损坏 | 主开关变"配置不可用：…" | 不动 |
| 用户取消 UAC | "管理员权限被取消，状态未改变" | 不动 |
| `reconcile` 验证未通过 | 气泡列出错误与 `verdictText` | 可能部分生效，图标转琥珀/红 |
| 计划任务未注册 | 菜单第三行常驻警告 | 自动切换不会发生（需 `task install --apply`） |
| v1 闸门仍启用/读不到 | 同上，标"可能冲突" | 两个调度并存的风险显式暴露 |

---

## 6. 自检

```powershell
QuietMode.exe --diag      # 写 %APPDATA%\QuietMode\diag.txt：status/validate/task/spin + 最近 120 行日志
QuietMode.exe --status    # 只把 status 的原始 JSON 写到 %APPDATA%\QuietMode\status.json
QuietMode.exe --selftest  # 真建托盘与菜单、刷一轮状态、2.5 秒后自己退出（冒烟测试）
```

`--diag` / `--status` / `--selftest` 都是只读的，不参与单实例互斥，托盘在跑时也能执行。

---

## 7. 已知取舍

- **每次切换都会弹一次 UAC**。v1 在"夜间自动静音中手动关掉"时只还原风扇、不弹 UAC；
  v2 把"写意图 + 执行 + 验证"合成一次提权调用，代价是一次确认框，换来"状态和结果不会分裂"。
- **托盘每分钟 `status` 一次**（spawn 一个 qmctl），实测约 1–1.5 秒，后台线程执行，不阻塞菜单。
- **`fan.autoSwitch` 字段仅用于承接 v1 迁移**，实际调度只看 `schedule.*`；设置窗口不暴露它。
