# QuietMode ACL 真机验收 — 本机隔离执行记录

> 对象：`powershell/Test-QuietModeAclApply.ps1`
> 目的：在**不需要虚拟机、也不碰生产对象**的前提下，把「PowerShell 7.7.0 下真实写
> 任务 ACL」这件事从推断变成实测。
> 本文件记录的是**实际跑过的**与**尚未跑到的**，两者分开写，不混。
> 最后一次更新：2026-11-15 18:0x —— 五档已全部在本机跑完，第 9 节的未完成项清零。

---

## 1. 结论摘要

| 项 | 状态 |
|---|---|
| 测试入口实现 | **完成** |
| 生产保护（运行时断言） | **完成**，有反例测试 |
| PowerShell 5.1 / 7.7.0 语法与静态契约 | **通过**（33 条，见 8 节） |
| 本机 `-Preflight` | **执行了**：提权会话下 10/10 OK，退出码 0 |
| 本机 `-Apply` | **已执行并通过**：真实写入三面 + 回读 + 权限边界 + 幂等，退出码 0 |
| 本机 `-Check` / `-Rollback` / `-Cleanup` | **已执行并通过**：三个都是退出码 0 |
| CI 上真实 ACL 写入 | **已执行并通过**：两条 Windows 腿各真跑一遍写入 + 回读，`196 / 193 / 0 / 3`（8.2 节） |
| 生产对象 | **未被触碰**：`\QuietMode\` 1 个任务、`\QuietHDD\` 2 个任务，6 项快照在 Apply 与 Cleanup 各比对一次全等 |

一句话：**五档全部在本机管理员 PowerShell 7.7.0 下真跑过，退出码全 0；「7.7.0 下
真实写任务 ACL」这件事不再有推断成分。**

过程里真实执行抓到了三个只有真跑才会出现的缺陷：Pester 的 discovery 求值陷阱
（CI 首轮）、`Grant-TaskRead.ps1` 里 `$check` 撞 `[switch]$Check` 导致写入路径根本
走不完（CI 第二轮）、以及 Rollback 用完整 SDDL 做全等比较被一个 `AI` 控制位永远判
失败（本机首轮）。三个都修了，修法都有回归守卫。细节在 6.8 与 8.1。

---

## 2. 环境

| 项 | 值 |
|---|---|
| 操作系统 | Microsoft Windows NT **10.0.26200.0** |
| PowerShell（被测） | **7.7.0** Core |
| PowerShell（回归） | 5.1.26100.9711 Desktop |
| Pester | 5.7.1（两版本一致） |
| 主机 | 本机日常主机，**非一次性环境** |
| 执行会话权限 | **非管理员**（`IsInRole(Administrator)` = False） |
| 任务计划服务 | Running |
| QuietMode 配置 | `C:\ProgramData\QuietMode\config.json` 存在（只读识别，未写入） |

主机名与账号名不记录在这里。完整记录由脚本自己写进
`%ProgramData%\QuietMode-AclTestRecovery\<GUID>\report.md`。

---

## 3. 测试对象

| 项 | 值 |
|---|---|
| 测试文件夹 | `\QuietMode-ACL-Test-c26ff7c5-1908-461e-a1b7-d6ccbee8c5b1` |
| 测试任务 | `QuietMode-ACL-Probe` |
| 任务文件 | `C:\Windows\System32\Tasks\QuietMode-ACL-Test-c26ff7c5-...\QuietMode-ACL-Probe` |
| 初始化 SDDL | `D:PAI(A;;FA;;;BA)(A;;FA;;;SY)` —— 刻意不含任何 `BU` 读 ACE |
| 状态 | Disabled |
| 触发器 | **无**（脚本里连 `Triggers.Create` 都不出现，有测试断言） |
| 动作 | `%SystemRoot%\System32\cmd.exe /c exit 0` |
| 运行身份 | SYSTEM / Highest（与生产任务同形态，但不调用 Worker） |
| 恢复资料 | `%ProgramData%\QuietMode-AclTestRecovery\<GUID>\state.json` |

`<GUID>` 每次 `-Preflight` 生成一次并写进 `current.json`，后续 `-Apply` / `-Check` /
`-Rollback` / `-Cleanup` 不带 `-TestId` 时沿用同一个 —— 五个模式必须落在同一个对象上，
否则「回滚」会去恢复一个不存在的夹具。

### 为什么初始 SDDL 要显式指定

不指定的话，服务会按系统默认策略生成 SD，而那个形态随 Windows 版本变化。
拿它当基线有两个后果：测试可能一开始就 `AlreadyCompliant`（等于什么都没测到），
或者换台机器结论就变。显式写一个「只有 BA 与 SY 全控」的 DACL，夹具的起点就是确定的。

建完立刻回读，断言文件夹与任务对象的 SD 里**都没有** `BU` 读 ACE —— 夹具不合格就 `throw`，
不进入授权步骤。

---

## 4. 生产基线（Apply 前快照）

脚本 `-Preflight` 会把 `\QuietMode` 与 `\QuietHDD` 下每个任务的**任务对象 SDDL**
与**任务文件 ACL** 各取一份，只用于「事后逐字节比对」。本机实测 6 项。

**这里只记结构，不记原文。** 本仓库是公开的，真实主机上生产任务的 DACL 属于本机
配置细节 —— 把 SDDL 原文贴出来，等于对外公布「这台机器的计划任务给哪些组开了什么
权限」。原文只留在本机（`-Preflight` 在本机打印，`-Apply` / `-Cleanup` 拿它做前后
比对），文档里只留足以判断「有没有被改动」的结构摘要：

| 对象 | 形态 | Allow ACE 数 | 结构摘要 |
|---|---|---|---|
| `\QuietMode\QuietMode-Reconcile` | 任务对象 | 7 | BA / SY 各有 FA；BU 有 FR（含一条 `ID` 继承项）；另有 BA / SY 的 `ID` 继承项 |
| 同上 | 任务文件 | 7 | `D:AI` 自动继承；BA / SY 为 FA，BU 为 FR，另有 `ID` 继承项 |
| `\QuietHDD\QuietHDD_DiskGate` | 任务对象 | 5 | `D:AI`；BU / SY 为 FR，BA 自身只有 `ID` 继承项 |
| 同上 | 任务文件 | 6 | `D:AI`；SY / BU 为 FR，另有 `ID` 继承项 |
| `\QuietHDD\QuietHDD_NightGate` | 任务对象 | 5 | 与 DiskGate 同形 |
| 同上 | 任务文件 | 6 | 与 DiskGate 同形 |

要点是前两行里的 `BU` + `FR` —— **生产任务本来就已经给 Users 读了**（早先补授权的
产物）。所以 `Grant-TaskRead.ps1 -Apply` 在生产文件夹上通常直接走「已有 Users 读，
跳过文件夹」分支；这正是 `$check` 那个缺陷在本机怎么跑都碰不到的原因（8.1）。

这份快照在 `-Apply` / `-Cleanup` 里于**动作前后各取一次**并逐键比对，
差异会以 `FAIL 生产对象被改动：<路径>` 的形式报出来。这是「没碰生产」的证据本身，
而不是一句承诺。

`\QuietHDD\` 两个闸门目前是 Disabled 状态 —— 脚本只读它们的 SDDL，不碰启用状态。

---

## 5. Preflight 实测（已执行）

跑了两次，两次都正确 —— 差别只在权限。

### 5.1 非提权会话（17:1x）

| # | 检查项 | 结果 |
|---|---|---|
| 1 | 操作系统 Windows | OK — 10.0.26200.0 |
| 2 | PowerShell 7.7.0 | OK — 7.7.0 |
| 3 | 管理员权限 | **FAIL — 未提权** |
| 4 | 生产授权脚本在位 | OK |
| 5 | Task Scheduler 服务运行 | OK |
| 6 | COM 可连 + 根文件夹 SD 可读 | OK — `flags=15` |
| 7 | QuietMode 安装状态可识别 | OK — `config.json` 存在 |
| 8 | 恢复根可写 | OK — `%ProgramData%\QuietMode-AclTestRecovery` |
| 9 | 测试命名空间空闲 | OK — 文件夹与文件路径都未占用 |
| 10 | 生产对象现状已记录 | OK — 6 项 |

**退出码 3。** 这是正确行为：管理员是硬前置条件之一，预检的职责就是回答「现在能不能跑」，
答案是不能就该红，不该给一个「差不多可以」的绿灯让人带着错觉进 Apply。

### 5.2 提权会话（17:34）

同一台机器、同一个脚本，在管理员 PowerShell 7.7.0 里 10 项**全 OK**，退出码 0：

```
OK   操作系统 Windows：10.0.26200.0
OK   PowerShell 7.7.0：7.7.0
OK   当前会话已提权
OK   生产授权脚本在位：…\powershell\Grant-TaskRead.ps1
OK   Task Scheduler 服务运行中
OK   任务计划器 COM 可用（根文件夹 SD 可读，flags=15）
OK   识别到 QuietMode 配置：C:\ProgramData\QuietMode\config.json（只读识别，不写它）
OK   恢复根可写：C:\ProgramData\QuietMode-AclTestRecovery
OK   测试命名空间空闲：\QuietMode-ACL-Test-c26ff7c5-…
OK   任务文件路径未被占用：…\System32\Tasks\QuietMode-ACL-Test-c26ff7c5-…\QuietMode-ACL-Probe
OK   已记录生产现状：6 项（用来证明本次没碰它们）
```

同一次预检还把生产对象 6 项快照落到了报告里，并生成了 TestId 写进
`current.json` —— 后续四档复用同一个测试对象，不会各跑各的。

---

## 6. 五档实测（本机，管理员 PowerShell 7.7.0）— 全部通过

**2026-11-15 17:34 与 17:52 在本机真实执行完毕，五档退出码全 0。**
`MANUAL_REQUIRED` 在这一轮被消掉了。

执行方式是一个提权驱动脚本（`dist/acl-apply/Run-AclApplyAll.ps1`，`dist/` 已在
`.gitignore` 里，不入库也不进发布包）：逐档起子进程、各取退出码、**Rollback 非 0
就跳过 Cleanup**。不逐行粘贴的原因是 `if (...) { return }` 在控制台里 `return`
不会中断后续行，而 `Test-QuietModeAclApply.ps1` 每档末尾有 `exit`，直接 `&` 调用
会把交互窗口一起带走。

```
  OK    Preflight  退出码=0
  OK    Apply      退出码=0        耗时=2s
  OK    Check      退出码=0        耗时=1s
  OK    Rollback   退出码=0        耗时=0s
  OK    Cleanup    退出码=0        耗时=0s
```

TestId `c26ff7c5-1908-461e-a1b7-d6ccbee8c5b1`，测试对象
`\QuietMode-ACL-Test-c26ff7c5-…\QuietMode-ACL-Probe`。

### 6.1 Apply：三个面都写进去，也都回读到了

夹具起点（脚本自己建的，两个面都不含 Users 读，符合预期）：

```
文件夹 SD : D:PAI(A;;FA;;;BA)(A;;FA;;;SY)
任务   SD : D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;SY)
文件 ACL  : D:PAI(A;;FA;;;SY)(A;;FA;;;BA)
```

调生产实现 `Grant-TaskRead.ps1 -Folders <测试文件夹> -Apply -IncludeTasks
-TaskNames QuietMode-ACL-Probe`，退出码 0。它自己打印的动作：

```
DONE 已追加 : D:PAI(A;OICI;FR;;;BU)(A;;FA;;;BA)(A;;FA;;;SY)
任务文件 : 共 1 个，补授权 1 个
DONE QuietMode-ACL-Probe
任务对象 : 共 1 个，补授权 1 个，失败 0 个
```

三个面回读（不是凭 `Set-Acl` 没抛异常判定）：

```
Apply 后 任务 SD   : D:AI(A;;FR;;;BU)(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;SY)
Apply 后 文件夹 SD : D:PAI(A;OICI;FR;;;BU)(A;;FA;;;BA)(A;;FA;;;SY)
Apply 后 文件 ACL  : D:AI(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)(A;ID;FR;;;BU)
```

| 断言 | 结果 |
|---|---|
| 任务对象 `GetSecurityDescriptor(0xF)` 完整读取（含 SACL） | OK —— 提权会话下**走的是完整路径**，没有退回 DACL-only |
| 任务对象：Users 有读权限（Task Scheduler API 回读） | OK |
| 测试文件夹：Users 有读权限 | OK |
| 任务文件：Users 有读权限（`Get-Acl` 回读） | OK |

第 9 节原来挂着的第 3 项（「0xF 在提权会话下的实测输出」）至此也有了答案：
它返回 `O:BAG:…D:AI(A;;FR;;;BU)…`，**含 owner/group 段但不含 `S:`** ——
说明本机这个任务对象的 SACL 为空。这恰好印证了 7.4 那条取舍：把 0xF 的结果
当恢复基线是危险的（有 SACL 时需要 `SeSecurityPrivilege` 才能写回），
所以基线只存 DACL，0xF 仅作证据字段。

### 6.2 权限边界：只给了读，没给任何写或改

```
OK   任务对象：Users 的 1 条 ACE 全为只读（FR）
OK   任务文件：Users 的 2 条规则全为只读（Read, Synchronize, Read, Synchronize）
OK   任务对象：没有 FA/FW/GA/WD/WO/SD 任何一个写或改权限别名
```

文件夹是 `A;OICI;FR;;;BU` —— 只读 + 容器继承，同样不含写。

### 6.3 幂等：第二次 Apply 走 Skipped，四个面零漂移

```
OK   第二次 Apply 退出码 0
OK   第二次 Apply 明确报告「已有 Users 读」，走的是 Skipped 分支
OK   任务对象 SD 两次一致
OK   文件夹 SD 两次一致
OK   任务文件 ACL 两次一致
     任务对象里 Users Allow ACE 条数：1（重复执行不应增加）
```

**这条是这轮验收里最有价值的一项。** 生产安装器会在每次 `Apply` 时调
`Grant-TaskRead.ps1`；如果它不幂等，ACE 会随每次重装/修复不断堆积。
计数器验的是「1 条」，不是「有 1 条」。

### 6.4 Check 只读：动作本身不改状态

```
OK   Check 前后任务对象 SD 一致
OK   Check 前后文件夹 SD 一致
OK   Check 前后任务文件 ACL 一致
```

同时 `Grant-TaskRead.ps1 -Check` 以只读方式枚举到 `QuietMode-ACL-Probe [Disabled]`，
退出码 0 —— 这是「授权之后普通用户真的能枚举到」的直接证据。

### 6.5 Rollback：三个面都回到 Apply 之前

```
原始文件夹 SD : D:PAI(A;;FA;;;BA)(A;;FA;;;SY)
原始任务   SD : D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;SY)
原始文件 ACL  : D:PAI(A;;FA;;;SY)(A;;FA;;;BA)

OK   任务对象 ACE 已与原始值一致，无需改写
WARN 控制标志：D: -> D:AI（SE_DACL_AUTO_INHERITED，不影响任何 ACE）
OK   恢复后原始 ACE 逐条在位
OK   恢复后新增的 Users 读 ACE 已按原始状态清除
OK   文件 ACL 已是原始状态，无需改写
OK   文件夹 SD 已是原始状态
```

**那条 WARN 是本轮发现的第二个真缺陷，见 6.8。** 恢复资料归档在
`%ProgramData%\QuietMode-AclTestRecovery\history\`，`state.json` 的 `phase`
字段落到 `Restored`。

### 6.6 Cleanup：对象消失了，生产没变

```
OK   已注销 QuietMode-ACL-Probe
OK   已删除 \QuietMode-ACL-Test-c26ff7c5-…
OK   测试文件夹不存在
OK   任务文件不存在
OK   System32\Tasks 下无残留目录
OK   生产对象 6 项逐个一致
```

`state.json` 与 `report.md` 归档到 `history\<GUID>-*`，`current.json` 指针保留，
本次目录清空 —— 报告不随清理消失。

### 6.7 生产对象：两次测试都没碰

`\QuietMode\` 1 个任务 + `\QuietHDD\` 2 个任务，每个取任务对象 SDDL 与任务文件
ACL 共 6 项，在 Apply 与 Cleanup 里各取一次，逐个比对全等。

独立佐证（测试结束后从外部再核一遍）：

| 检查 | 结果 |
|---|---|
| 三个生产任务文件的 Users ACE 条数 | 仍是 2 条 `Read, Synchronize`，与测试前一致 |
| `state.json` 的 mtime | 停在 `2026-11-15 10:20:12`（今早部署时写入），17:34 与 17:52 两轮测试全程未变 |
| `System32\Tasks` 下的测试目录 | 无残留 |

### 6.8 本轮暴露并修掉的两个真缺陷

真实执行比纸面推演多抓到两件事，都属于「只有真跑才会出现」的类型。

**缺陷 A —— `[switch]$Check` 与 `$check` 是同一个变量。**
`Grant-TaskRead.ps1` 声明了 `[switch]$Check`，写入路径却写着
`$check = Wait-SecurityDescriptor …`。PowerShell 变量名不区分大小写，等于往一个
带类型约束的 switch 变量里塞 SDDL 字符串，抛
`ArgumentTransformationMetadataException: Cannot convert value "System.String"
to type "System.Management.Automation.SwitchParameter"`。后果是 `-Apply`
**死在「写后回读」这一步，退出码 1，真实写入从来没被回读验证过。**

为什么本机一直没暴露：写入路径要求「已提权 **且** 目标文件夹还没有 Users 读」，
本机先前只跑 DryRun 与「已有 Users 读，跳过」分支，永远碰不到这几行。
CI 打开 `QM_ALLOW_TASK_INTEGRATION=1` 之后第一次真跑就炸了（8.1 节）。

修法：改名 `$sdAfter`；另加一条 AST 守卫，扫全仓 `.ps1`/`.psm1`，任何脚本给与
自己 `[switch]` 形参同名的变量赋非布尔值即失败。守卫做过变异测试 —— 退回修复
前版本，精确报出那两行。

**缺陷 B —— 用完整 SDDL 串做全等比较，恢复永远判失败。**
原始任务 SDDL 是 `D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;SY)`，`SetSecurityDescriptor`
写回后读出来是 `D:AI(…)` 同样三条。差别只有控制位 `SE_DACL_AUTO_INHERITED`：

- 夹具用 `RegisterTaskDefinition` 传无 `AI` 的串能原样存下
- 一经 Schedule 服务写回，读回来必然带 `AI`

这一位不受调用方控制。后果是 `-Rollback` 永远 FAIL，而 `-Cleanup` 要求
`phase=Restored`，于是永远被跳过 —— **整条链卡死在一个标志位上。**

修法：新增 `Split-DaclSddl` / `Compare-DaclSddl`，任务对象改成按
**ACE 值 + 出现次数**比较（顺序无关），控制标志差异降级为 WARN。用计数而不是
「包含」，是因为同一张 DACL 理论上可以出现两条同值 ACE，只判断存在会把
「2 条变 1 条」当成通过。

文件夹与文件**仍然用字符串全等** —— 实测两者都能精确恢复，没有理由放宽。

### 复现方式

需要一个管理员 PowerShell 7.7.0 窗口，逐档跑：

```powershell
cd 'H:\AI工作区\电脑相关\_publish\quiet-windows-toolkit'
$s = '.\powershell\Test-QuietModeAclApply.ps1'

pwsh -NoProfile -File $s -Preflight      # 只读，10 项前置，非 0 就别往下走
pwsh -NoProfile -File $s -Apply          # 真实写入 + 幂等 + Check 比对
pwsh -NoProfile -File $s -Check          # 只读核对
pwsh -NoProfile -File $s -Rollback       # 三个面恢复
pwsh -NoProfile -File $s -Cleanup        # 注销任务、删文件夹
```

退出码语义：`0` 通过 / `2` 有失败项 / `3` 前置不满足。
任一档 `2` 就停下，恢复资料原地保留，按打印的原始 SDDL 可以手工复原。

### 各档会打印什么

每一段都带 `OK` / `FAIL` / `WARN` 前缀，重点看这几行：

| 输出 | 含义 |
|---|---|
| `夹具就绪：文件夹与任务对象都不含 Users 读 ACE` | 起点正确，后面测的才算数 |
| `任务对象安全描述符通过 GetSecurityDescriptor(0xF) 完整读取（含 SACL）` | 提权会话应看到这行 |
| `任务对象：Users 的 N 条 ACE 全为只读（FR）` | 权限没扩大 |
| `任务对象：没有 FA/FW/GA/WD/WO/SD 任何一个写或改权限别名` | 「能改任务」的位一个都没给 |
| `任务对象：原有 ACE 一条不少` | 不是覆盖式写入 |
| `第二次 Apply 明确报告「已有 Users 读」，走的是 Skipped 分支` | 幂等 |
| `任务对象 SD / 文件夹 SD / 任务文件 ACL 两次一致` | 重复执行不漂移 |
| `Check 前后任务对象 SD 一致` | 检查动作不改状态 |
| `生产对象 N 项逐个一致` | 没碰生产 |

任何一行 `FAIL` 都会让退出码变成 2，并保留恢复资料。

### 报告在哪

每档都会往 `%ProgramData%\QuietMode-AclTestRecovery\<GUID>\report.md` 写一份自动记录
（模式、退出码、失败与警告条数、关键字段）。`-Cleanup` 再把 `state.json` 与
`report.md` 归档到 `history\<GUID>-*.json|md`，目录本身清空。

本次归档后的关键字段：

```
phase                = Restored
rollback_task        = already
rollback_file        = already
rollback_folder      = already
production_unchanged = True
```

`report.md` 是脚本自动写的，本节是人写的，两者互为佐证。

---

## 7. 脚本已实现的验证项（代码层面，已被测试覆盖）

这一节列的是**代码里做了什么**，与 6 节的实际输出互为对照 ——
免得把「代码写了」当成「已经验过」，也免得把「验过了」说成「碰巧过了」。

### 7.1 生产保护

每次操作任务对象前过 `Assert-TestScoped`，四条同时成立才放行：

1. 任务文件夹必须以 `\QuietMode-ACL-Test-` 开头；
2. 必须**严格等于** `\QuietMode-ACL-Test-<当前 TestId>`；
3. 不得落在 `\Microsoft\Windows`、`\QuietHDD`、`\QuietMode` 任一前缀之下；
4. 任务名必须是 `QuietMode-ACL-Probe`。

`TestId` 另有一道：必须是标准 GUID（`8-4-4-4-12`），因为它会被拼进文件夹名。
`-TestId 'a\..\..\QuietMode'` 这类值在格式校验就被拒，连任务计划器都碰不到。

不满足就 `throw`，不返回 bool —— 避免「调用方忘了看返回值」这种漏网。

已测：`-TestId` 传 `\QuietMode` / `QuietMode` / `..\QuietMode` / `not-a-guid` /
`a\..\..\QuietMode` 全部 `exit 3`，输出 `不是合法 GUID`。

### 7.2 不复制授权实现

本脚本里**一行 `AddAccessRule` 都没有**（有静态断言）。真正写 ACL 的动作全部发生在
子进程调用的 `Grant-TaskRead.ps1` 里 —— 与安装器 `[15]` 步同一个脚本、同一批函数、
同样的参数形态（`-Folders` / `-Apply` / `-IncludeTasks` / `-TaskNames`）。

复制一份简化实现进测试脚本，测的就是复制品。这条是设计约束，不是风格偏好。

### 7.3 三面回读

| 面 | 读法 | 判据 |
|---|---|---|
| 任务对象 SD | `IRegisteredTask::GetSecurityDescriptor`，主判据 `4`（DACL），证据另取 `0xF`（含 SACL） | `Test-QmUsersReadAce`（权限位，不是「有没有 BU」） |
| 测试文件夹 SD | `ITaskFolder::GetSecurityDescriptor` | 同上 |
| 任务文件 ACL | `Get-Acl` 重新读取 | `Test-QmFileHasUsersRead`（`FileSystemRights` 的 `ReadData` 位） |

任务文件**必须重新 `Get-Acl`**，不能凭 `Set-Acl` 没抛异常就判定成功 ——
2.0.5 修掉的正是这个缺陷（`Grant-TaskFileRead` 现在写后回读，读不到就 `throw`）。

### 7.4 基线只存 DACL

`GetSecurityDescriptor(0xF)` 读出来的串含 `S:`（SACL）段。拿它去
`SetSecurityDescriptor` 需要 `SeSecurityPrivilege` —— 存一个自己写不回去的基线，
等于没有基线。所以：

- `state.json` 里的 `taskSddlBefore` / `folderSddlBefore` / `fileAclSddlBefore`
  一律是 DACL-only；
- `0xF` 的结果另存 `TaskSddlFull` / `FolderSddlFull`，**只作证据记录**。

测试本来也只改 DACL，恢复同样只在 DACL 范围内，不越界碰 owner / SACL。

### 7.5 权限越界判据

不只看「Users 有没有读」，还看「有没有多给」：

- 任务对象：每条 `BU` Allow ACE 的权限串必须是 `FR` 或 `GR`；出现 `FA` / `FW` / `GA` /
  `WD` / `WO` / `SD` 或任何非 `0x1` 的十六进制值一律记 `FAIL`。
- 任务文件：`FileSystemRights` 去掉只读位（`ReadData` / `ReadAttributes` /
  `ReadExtendedAttributes` / `ReadPermissions` / `Synchronize`）之后还有剩余，记 `FAIL`。

ACL 写入最坏的失败形态不是「没加上」，是「加多了」—— 加多了等于给普通用户改任务的权限。

### 7.6 原有 ACE 保留

把 Apply 前的每条 `(...)` ACE 拿出来，逐条在 Apply 后的 SDDL 里查。
少一条就是 `FAIL`。这条防的是「`SetSecurityDescriptor` 把 DACL 覆盖成只剩新 ACE」
那种最危险的失败形态。

### 7.7 幂等

第一次 Apply 成功后立即再跑一次相同命令，断言：

- 退出码 0；
- 输出里有「已有 Users 读」（走的是 Skipped 分支，不是又加一条）；
- 任务 SD / 文件夹 SD / 文件 ACL 与第一次完全一致；
- `BU` Allow ACE 条数不变。

### 7.8 Check 只读

`-Check` 自己做一次快照 → 调生产脚本的 `-Check` 只读路径 → 再快照 →
三个面逐项比对。不一致就是 `FAIL`。

这条的意义是把「检查」这个动作本身也纳入不变量：一个会改状态的检查工具，
在排障时比没有还危险。

### 7.9 失败即留证据

`try/finally` 保证 COM 对象释放；恢复资料一落盘就不再删，除非 `-Rollback` 全部
验证通过并且 `-Cleanup` 明确执行。恢复失败时输出里直接给原始 SDDL，
可以照着手工 `SetSecurityDescriptor` 回去。

`-Cleanup` 还多一道：`state.json` 的 `phase` 不是 `Restored` 就拒绝执行 ——
硬删会把「改过 ACL 的残留」变成「消失了」，两条都不好查。

---

## 8. 回归测试

本机（非提权），双版本逐项对齐：

| 项 | 结果 |
|---|---|
| PowerShell 5.1 全量 Pester | `Total=196 Passed=192 Failed=0 Skipped=4` |
| PowerShell 7.7.0 全量 Pester | `Total=196 Passed=192 Failed=0 Skipped=4` |
| 新增 `Test-QuietModeAclApply.Tests.ps1` | **33 条，双版本全过** |
| 新增 switch 撞名守卫（见 8.2） | **1 条，附变异测试** |
| 原有测试 | **一条没删**（从 162 增到 196，净增 34） |
| C# 单测 | **123 / 123 通过** |
| 双版本语法解析（全仓 PS1/PSM1） | 0 错误 |

4 条跳过仍是 `Grant-TaskRead.Tests.ps1` 里需要提权 + `QM_ALLOW_TASK_INTEGRATION=1`
的真实任务 ACL 集成用例。**本机不设这个开关**（日常主机，不为测试去建真实任务）。

**CI 那边已经打开了这个开关。** `windows-latest` runner 本身提权，
以前不设，等于把这层真实写入覆盖永久留在未验证状态。现在两条 Windows 腿都会
真跑那 4 条：建唯一前缀的临时任务文件夹 → 真的 `SetSecurityDescriptor` / `Set-Acl` →
回读验证 ACE 在位、权限没扩大、重复执行不重复加 → `AfterAll` 清理。
对象名是 `\QuietMode-Test-<guid>\`，不碰 runner 上任何既有任务。

也就是说：真实 ACL 写入在 **PS 5.1 与 7.7.0 两条腿上各有一次自动化执行**，
与 6 节的 `-Apply` 互补 —— 前者验证「授权链路」，后者验证「完整五模式 + 生产比对」。

### 8.1 CI 第一次真跑就抓到两个缺陷

把开关打开不是走过场 —— 前两轮 CI 各暴露一个「本机永远碰不到」的问题，两个都修了。

**第一轮（`f00a1c5`，BeforeDiscovery 修复之前，`Total=185 Passed=178 Failed=0 Skipped=7`）：
4 条集成用例一条都没执行。** 判据写在了 `BeforeAll` 里，而 `It ... -Skip:(expr)`
的表达式在 **discovery 阶段**求值 —— 那时 `BeforeAll` 还没跑，变量恒 `$null`，
`-not $null` 就是 `$true`，于是**任何环境都跳过**。修法是把判断搬进
`BeforeDiscovery`（`79c4e6f`）。这类「本地跳过、CI 也跳过」的假绿最危险：
数字看起来正常，覆盖面是零。

**第二轮（`Total=185 Passed=180 Failed=2 Skipped=3`）：两条真实写入用例失败。**
报错是

```
Grant-TaskRead.ps1 : Cannot convert value "System.String" to type
"System.Management.Automation.SwitchParameter". ...
```

根因不在测试侧，在**生产脚本**里：`Grant-TaskRead.ps1` 声明了 `[switch]$Check`，
而写入路径写着 `$check = Wait-SecurityDescriptor ...`。PowerShell 变量名不区分
大小写，`$check` 就是 `$Check` —— 一个带类型约束的 switch 变量，往里塞 SDDL
字符串会抛 `ArgumentTransformationMetadataException`，`-Apply` 在「写后回读」
这一步直接终止（退出码 1）。

为什么本机从未暴露：这条路径要求同时满足「已提权」**和**「目标文件夹还没有
Users 读」。本机 DryRun 打的是 `\QuietHDD\`，走「已有 Users 读，跳过文件夹」
分支，永远碰不到这几行。**这正是任务书那句「不能凭 Set-Acl 未抛异常判定成功」
的镜像问题 —— 这次是「回读那一步自己崩了，于是连回读都没发生」。**

修法（`cd4d060`）：两处变量改名 `$sdAfter`；另加一条 AST 守卫扫
`powershell/` 下全部 `.ps1`/`.psm1`，任何脚本只要给与自己 `[switch]` 形参同名的
变量赋**非布尔**值就失败。放行形态按本机实测确定 —— `[bool]` 强转与 `$true`/
`$false` 字面量合法，**Int32 的 `1`/`0` 反而抛错**（参数绑定文案里那句
「1 或 0」不适用于变量赋值）。守卫做过变异测试：把源码退回修复前，它精确报出
那两行；退回修复后，全仓 28 个脚本 0 命中。

### 8.2 CI 实测数字（最终）

run [`43700258531`](https://github.com/jmwangde3/quiet-windows-toolkit/actions/runs/43700258531)，
HEAD `fc2307f`，三条腿全绿：

| 腿 | Pester |
|---|---|
| `windows / PowerShell 5.1 (Desktop)` | `Total=196 Passed=193 Failed=0 Skipped=3` |
| `windows / PowerShell 7.7.0 (pwsh)` | `Total=196 Passed=193 Failed=0 Skipped=3` |
| `linux / core compile only` | 编译通过 |

这一组之前的三次运行（`cd4d060`、`09e64c0`、`61aa8c3`）都是全绿，数字同样是
`186 / 183 / 0 / 3`；`fc2307f` 新增 10 条 DACL 比较用例后变成 `196 / 193 / 0 / 3`，
Passed 恰好 +10、Skipped 不变 —— 说明新增用例全部真执行，没有一条被跳过掩盖。

本机双版本在最终 HEAD 上是 `Total=196 Passed=192 Failed=0 Skipped=4`（差的 1 条
是「未提权 -Apply 返回 3」，本机非提权环境下反而可执行，CI 提权 runner 上转跳过）。

上表不是从日志里抄一行就完事 —— NUnit XML 逐用例核过：196 条 = 193 `Success` +
3 `Ignored`。3 条 `Ignored` 全部是「只在未提权时才成立」的用例，在提权 runner 上
按设计跳过：

- `Grant-TaskRead 脚本契约.未提权时 -Apply 必须返回 3，不能假装成功`
- `Test-QuietModeAclApply…-Apply / -Rollback / -Cleanup 在未提权时都返回 3`
- `Test-QuietModeAclApply…-Apply 未提权时输出里点明「未提权」，且不建立夹具`

真实任务集成那 5 条**全部 `Success`**，包括上一轮挂掉的两条：

- `临时对象名带唯一前缀，不会撞到生产任务`
- `Apply 后文件夹 SDDL 含 Users 读 ACE，且原有 ACE 一条不少` ← 上一轮失败
- `任务对象 Apply 后也含 Users 读 ACE` ← 上一轮失败
- `重复执行不重复添加 ACE（第二次应报「已有 Users 读」）`
- `Users 拿到的只是读权限，不含修改/删除/启停任务`

`fc2307f` 新增的 10 条 `DACL 比较（AI 位不算恢复失败）` 用例也全部 `Success`，
其中一条是把 6.8 缺陷 B 那次真机失败的**两个原始串**直接写成断言：

```
复现 11-15 真机那次 Rollback 的两个串 —— 必须判通过
恢复后还带着 Users 读 ACE —— 必须判失败，并指出多出哪一条
丢掉一条原始 ACE —— 必须判失败，并指出缺哪一条
ACE 顺序变化不算差异（规范化会重排，权限语义不变）
重复 ACE 按条数比较，不是按「存在」
Rollback 的任务对象判据不再用字符串全等 —— 回退这行会重新撞上 AI 位
```

最后一条是防回退守卫：把判据改回 `$last -eq $state.taskSddlBefore` 就立刻失败。
事故的两个串被固化进测试，这是「同类问题不会复活」的实质保证，不是口号。

结论：**PS 5.1 与 7.7.0 两条腿上，真实的 `SetSecurityDescriptor` 与 `Set-Acl`
写入各被真跑过一遍，并通过回读验证「新增 ACE 在位 + 原有 ACE 没丢 + 权限没扩大 +
重复执行不重复加」。** 这与 6 节本机五档是两层不同的证据：CI 证明「授权链路在
7.7.0 上可写可回读」，本机 `-Apply` 证明「五模式 + 生产比对 + 恢复通道在真实主机上
成立」。两层现在都是绿的，且互相独立。

---

## 9. 未完成事项

**没有未完成项了。** 原本挂着的三项都已关闭：

| # | 项 | 怎么关闭的 |
|---|---|---|
| 1 | 本机 `-Apply` 真实写入 | 2026-11-15 17:34 在管理员 PowerShell 7.7.0 里跑完，退出码 0（6.1 – 6.3） |
| 2 | 本机 `-Check` / `-Rollback` / `-Cleanup` 实跑 | 同一轮跑完，三个都是退出码 0（6.4 – 6.6） |
| 3 | `GetSecurityDescriptor(0xF)` 在提权会话下的实测输出 | 输出 `O:BAG:…D:AI(A;;FR;;;BU)…`，**含 owner/group 但含 `S:` 为空** —— 本机该任务对象没有 SACL。这恰好印证 7.4：0xF 不能当恢复基线 |
| 4 | CI 集成开关打开后的首次结果 | 8.1 / 8.2 |

跑的过程中新暴露的两个缺陷（`[switch]$Check` 撞名、Rollback 全等比较）也都在
同一轮修掉并有回归守卫，见 6.8。

**一处推算失误，留档。** 上一版这里写过「4 条集成转执行、2 条未提权用例转跳过，
应为 `185 / 182 / 0 / 3`」。实际第一轮是 `185 / 178 / 0 / 7`（多 3 条跳过），
第二轮是 `185 / 180 / 2 / 3`。两个偏差各有原因：多出来的跳过是那个
discovery 阶段的 `-Skip` 陷阱；而「2 条未提权用例转跳过」也猜错了 —— 真正会在提权
环境转跳过的只有 1 条，另 2 条走的是运行期 `Set-ItResult -Skipped`，在提权 runner
上照样执行并通过。教训：**跳过数是推断出来的，就该在 CI 上核对，而不是写进结论**。

**另一处留档。** 18:0x 那次 Rollback 的 FAIL 报了 `任务对象恢复后与原始值不一致`，
但当时贴出的两个串在 ACE 层面完全相同，只差 `D:` 与 `D:AI`。第一反应是「判据太严」
（对），但如果当时顺手把判据放宽成「包含原始串就算过」，就会漏掉真正的隐患：
`SetSecurityDescriptor` 写回后 `AI` 位必然带上，这个位**不可控**。正确做法是把
「哪些差异可以容忍」写成显式规则（ACE 值 + 计数，顺序无关，控制标志降级为 WARN），
而不是把整串比较换成模糊匹配。

---

## 10. 风险与取舍

**为什么不用虚拟机。** 这台机器上的风险面其实很窄：测试对象是新建的、Disabled 的、
`cmd /c exit 0` 的任务，唯一被改的是它自己的 SDDL 与它自己的任务文件 ACL。
真正的风险是「改错对象」，而那件事由 `Assert-TestScoped` 的运行时代言挡住 ——
被测试覆盖的反例有 5 个。相比之下，装一台 VM 的成本换来的是同一条链路，
但没有人会真的去跑它。

**为什么不改生产任务做对照。** 任务书禁止，也没必要。生产任务的 SDDL 在 4 节里
只作快照，不参与任何写入。

**恢复根为什么不放生产配置目录。** `C:\ProgramData\QuietMode` 由安装器创建并做过
权限加固（Users 只读）。放那里会出现：非提权 `-Preflight` 写不进去、提权 `-Apply`
写进去、之后非提权 `-Rollback` 又读不回来。恢复通道不能依赖「你现在是不是管理员」。
所以固定在 `%ProgramData%\QuietMode-AclTestRecovery`，两个身份都能访问，
也从根上满足「不为跑测试去碰生产配置目录」。

**已知不覆盖的。** 本脚本只验 ACL 这一层。真实 `Install-QuietMode.ps1 -Mode Apply`
里的其它动作 —— 停用 v1 闸门、禁维护任务、改 SysMain、动真实磁盘、调 FanControl ——
**一个都没碰**，也不在本文件的结论范围内。那部分仍归
`docs/ACL_APPLY_HOST_VALIDATION.md` 之外的整体验收。
