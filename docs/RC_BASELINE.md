# QuietMode v2.0 RC 基线

> 生成时间：2026-11-12
> 用途：记录「这一版到底验证了什么、没验证什么」。任何后续改动以此文件为对照基线。

## 1. 版本与提交

| 项 | 值 |
|---|---|
| 仓库 | `https://github.com/jmwangde3/quiet-windows-toolkit` |
| 分支 | `codex/quietmode-v2-rc` |
| 基线提交（分叉点） | `7956d95` `fix(reconcile): verify against the target phase, not the action string`（= `origin/main`） |
| 产品版本 | `2.0.0-rc.1`（`ProductInfo.Version`） |
| 程序集版本 | `QuietMode.exe` 2.0.0.0 / `qmctl.exe` 1.0.0.0（未单独维护，见 §7） |
| 目标框架 | .NET Framework 4.8（编译期用 `Microsoft.NETFramework.ReferenceAssemblies`） |
| 语言级别 | C# 7.3（net481 + LangVersion 7.3，无 C# 8 语法） |
| PowerShell | 5.1 为主要目标；脚本另在 7.x 下验证过关键字路径 |

## 2. 构建

```
dotnet build QuietMode.sln -c Release
```

结果：**0 错误 0 警告**（2026-11-12 本机，Windows 11 26200 / .NET SDK 8）。

构建产物：

- `src/QuietMode.Core/bin/Release/net481/QuietMode.Core.dll`
- `src/QuietMode.Windows/bin/Release/net481/QuietMode.Windows.dll`
- `src/QuietMode.Cli/bin/Release/net481/qmctl.exe`
- `QuietMode/build/bin/Release/QuietMode.exe`

## 3. 测试

### 3.1 C# 单元测试

```
tests/QuietMode.Core.Tests/bin/Release/net481/QuietMode.Core.Tests.exe
```

**106 / 106 通过，退出码 0。**

覆盖：JSON 层、默认策略、校验器、原子写与 last-known-good、v1 迁移、state 往返、
基线完整性四态、磁盘身份解析（含「绝不回退 Disk 0」）、安全闸门、维护计划与验证
（夜间/白天/Critical 分级/原始状态未知）、时间窗口与开机补偿、状态机（含抑制窗口、
白天重试、漂移修复）、任务 XML、漂移分类、特权路径读写纪律、托盘状态契约。

### 3.2 只读集成测试

```
powershell\Test-QuietMode.ps1
```

**ALL PASS，退出码 0。** 分项：

| 步骤 | 结果 |
|---|---|
| 编译三种产物（Core.Tests / qmctl / 托盘） | PASS |
| Core 单测 | 106 / 106 |
| 电源模块 DISKIDLE 只读回读 | PASS（AC=1200s DC=1200s） |
| 配置校验 | PASS |
| baseline 校验 | PASS（本机旧 schema，报「不完整」属预期） |
| 托盘契约 status 字段 | PASS |
| 磁盘身份解析 | PASS（两块盘均唯一匹配） |
| 磁盘安全闸门（`Test-DiskSafety.ps1`） | 5 / 5，系统盘被拦住 |
| 维护管理（`Test-MaintenanceSafety.ps1`） | 6 / 6 |
| 维护任务三态（`Test-TaskState.ps1`） | 13 / 13（PS 5.1 与 7.x 均通过） |
| 统一调度（`Test-SchedulingSafety.ps1`） | 10 / 10 |
| 特权目录与攻击检测（`Test-Privilege.ps1`） | 10 / 10 |
| 维护计划与验证 | PASS（night 10 步 / day 11 步） |

### 3.3 脚本实跑（DryRun）

| 脚本 | 结果 |
|---|---|
| `Install-QuietMode.ps1 -Mode DryRun` | 退出码 0；0 失败 / 12 警告；未改动系统状态 |
| `Uninstall-QuietMode.ps1`（默认 DryRun） | 退出码 0；打印 7 步计划，未改动系统状态 |
| `Repair-QuietMode.ps1`（诊断模式） | 退出码 2；5 项失败全部是**真实的**配置根权限问题（见 §6），非脚本缺陷 |

## 4. 功能状态

| Phase | 内容 | 状态 |
|---|---|---|
| 0 | 代码审计与行为基线（LEGACY_AUDIT / LEGACY_BEHAVIOR / ARCHITECTURE） | 完成 |
| 1 | 统一配置与快照（config.json / baseline.json / 原子写 / 迁移） | 完成 |
| 2 | 磁盘管理（稳定身份、安全闸门、唯一写入口、回读验证） | 完成 |
| 3 | 维护管理（唯一清单、禁改范围、夜间禁用/白天恢复、三态验证） | 完成 |
| 4 | 统一 Worker 与单一调度任务 | 完成 |
| 5 | C# 托盘分层（只读观测，不再自己决策） | 完成 |
| 6 | 安装 / 迁移 / 回滚（`Install-QuietMode.ps1`） | 完成，脚本级 DryRun 已验证 |
| 6.1 | DISKIDLE 进入 reconcile 链 | 完成（本 RC 只验证，未重写） |
| 6.2 | 心跳日志 + `task status` 误报修复 | 完成 |
| 7 | 测试与 CI 收尾 | 本文档 + README/CHANGELOG 更新完成；未接入 CI 服务 |

## 5. RC1 可靠性修复（提交 `08d9a9d`）

| 编号 | 问题 | 处理 | 验证 |
|---|---|---|---|
| RC1-01 | 手动关闭静音后，抑制标记在白天恢复成功时被过早清除，本夜又自动重开 | 抑制绑定「夜间窗口身份」（窗口开始日期，`NightWindow.Key`），`state.autoSilentSuppressedWindow`（schema 3→4）；决策前 `NormalizeSuppression` 迁移/释放过期抑制；`ApplyRun` 的 day 分支不再清抑制 | 单测 4 项：只覆盖本夜、夜间重启保持、旧标记迁移、手动静音可覆盖 |
| RC1-02 | 漂移被当成二元「成功/失败」，读不到状态被当失败无限重试 | 新增 `DriftClassifier`：Compliant / ActionableDrift / UnfixableDrift / ObservationUnknown / SafetyBlocked；只有 ActionableDrift 允许自动重跑；`lastDrift` / `lastProblems` 持久化；心跳行带 `drift=` / `why=` | 单测 6 项（含「维护任务被重新启用=可修复」「身份不明磁盘=安全阻塞」「全员读不到=未知」） |
| RC1-03 | PowerShell 三态判断把 `$null` 当 `false`，权限不足被当成「任务不存在」 | `Get-QmTaskState` / `Invoke-QmStopTask` / `Invoke-QmSetTaskEnabled` 重写：COM HRESULT 低位码定性（2/3 = NotPresent，5 = 拒绝访问 = Unknown）；写入后回读仍为 null 判验证失败 | `Test-TaskState.ps1` 13 项，PS 5.1 与 7.x 均通过 |
| RC1-04 | 维护恢复的 `Critical` 标反了：原本禁用、恢复禁用失败被当关键失败 | `s.Critical = desired`（原本启用的任务恢复不回来才是关键） | 单测：Critical 分级 |
| RC1-05 | 只认得两条 v1 任务路径；夜间重复时长写死 10 小时 | `LegacyTaskPaths` 补齐 4 条；`TaskXml` 迁入 Core，夜间 Duration 按窗口计算（23:58→10:02 = `PT10H4M`），白天触发器加有界重试（`PT4H`/`PT30M`） | 单测 4 项（窗口时长、结构合法、白天重试有界、自定义时间保留） |
| RC1-06 | 损坏的 baseline 仍会被用于「恢复」；旧 schema 被判损坏 | 新增 `BaselineHealth` 四态（Missing/Corrupt/Partial/Complete）+ `TryLoad`/`Inspect`；`baseline.previous.json` 只在现基线可信时轮换；白天无可用基线判 Unknown | 单测：四态 + 旧 schema 兼容 + 新 schema 拒用 |
| RC1-07 | 验证失败后状态里还留着上一次的成功结论 | 每次 reconcile 都写 `logs/reconcile.log`（含 `none`）；`lastVerdict` 与真实状态一致；Worker 没产出结果文件判失败 | 单测 + 集成测试 `reconcile 演练不改变 state.json` |

## 6. RC2 安装 / 迁移 / 恢复 / 安全

| 编号 | 内容 | 状态 |
|---|---|---|
| RC2-01 | 安装器七段预检（环境 / 载体 / 现有安装 / 磁盘身份 / 静音窗口与风扇 / baseline 四态 / 安装计划）+ 备份 + 单调度不变量 | 完成，DryRun 实跑通过 |
| RC2-02 | v1 安全迁移 | 完成（见下） |
| RC2-03 | 卸载与恢复事务 + 体检修复脚本 | 完成，DryRun 实跑通过；`-Apply` 未实机执行 |
| RC2-04 | 特权目录安全 | 完成（ACL 检查 + 路径纪律 + 攻击自测）；**本机 ACL 尚未加固** |
| RC2-05 | DISKIDLE 行为保持 | 已验证未重写（见 §6.3） |

### 6.1 RC2-02 v1 迁移的具体保证

- 迁移前记录：`recovery\install-<时间戳>\migration-before.txt`，含 v1 三份配置原文、
  四条历史任务的状态、维护任务当时的观测值，并明确标注「观测值 ≠ 原始值」。
- 备份：`config.json` / `state.json` / `baseline.json` / `baseline.previous.json` /
  `settings.ini` / `state.ini` / `diskgate.conf` / 旧任务 XML，全部落在同一个备份目录。
- 磁盘：无法解析出稳定身份的编号被**整条丢弃**并告警，绝不退回 `Disk 0`。
- 原始维护状态：夜间窗口内拍快照时，被禁用的维护任务标记
  `originalUnknown=true`，白天保留 Pending 并说明，不启用也不禁用。
- 新旧调度：先注册 v2 并回读确认，再停 v1；v2 没落地就退出（不动 v1），
  v1 停不掉就回滚 v2（回到「只有 v1」），收尾再复核一次没有并行。
- 静音窗口原义：窗口时间原样迁移；v2 新增的开机补偿在迁移 note 里明确写出，
  不静默改变行为。

### 6.2 RC2-04 威胁模型与对策

SYSTEM 会读 `C:\ProgramData\QuietMode` 下的配置并据此操作任务、服务、注册表、磁盘。
因此任何「普通用户可写、SYSTEM 会执行」的路径都是提权通道。

对策（`QuietMode.Core.Configuration.PrivilegedIo`）：

1. 请求/结果文件名带随机 nonce，不可预测；
2. 写请求用 `FileMode.CreateNew` —— 目标已存在即失败，绝不覆盖、绝不跟随已有文件；
3. 目录或结果文件是重解析点（符号链接 / junction）直接拒绝。

检测能力自测（`Test-Privilege.ps1`，10 项全通过）：junction 识别、`..\` 逃逸、
下级目录逃逸、绝对路径、盘符形式、`.`/`..`、合法文件名放行、演练模式不改权限。

### 6.3 RC2-05 DISKIDLE 行为验证（只验证，未重写）

| 要求 | 验证方式 | 结果 |
|---|---|---|
| `spinDown.enabled=true` 时自动对账 | 单测「计划：停转开启时带上目标秒数」+ 实机 `spin show` | 通过；AC=DC=1200s 与配置一致 |
| `enabled=false` 时绝不写 | 单测「关闭时不下发」+ `spin show` 报 `reconcileManaged=false` | 通过 |
| 写入后回读 AC/DC，不符判失败 | `Invoke-QmSpinDown` 写后 `Get-QmDiskIdleSeconds` 复读；单测「DISKIDLE 与配置不一致算失败」 | 通过 |
| 读不到算降级而非失败 | 单测「DISKIDLE 读不到算降级」 | 通过 |
| 单项失败不阻断其它步骤 | `ReconcilePlanner` 步骤独立结算，验证器分级汇总 | 通过 |

## 7. 已知差距与未验证项

**必须如实标注：以下没有在本机实机验证，不得视为已通过。**

1. **`Install-QuietMode.ps1 -Mode Apply` 未执行。** 需要管理员，且会改系统状态；
   本次只跑 DryRun。Apply 路径的代码已被走读，但「第一次真跑」仍属未验证。
2. **`Uninstall-QuietMode.ps1 -Mode Apply` 未执行。** 同上。恢复顺序与回读逻辑只经过
   DryRun 与代码走读。
3. **配置根 ACL 尚未加固。** 本机实测 `config.json` / `state.json` / `baseline.json` /
   `run` / `recovery` 对普通用户可写 —— 这是**真实存在**的问题，
   `Repair-QuietMode.ps1 -Apply` 或安装器 Apply 时会加 Deny ACE，但本次未执行，
   因此加固后的行为（含日志目录放行追加）未经验证。
4. **`-Mode Rollback` 未执行。** 只做代码走读。
5. **真实夜间/白天切换未在本次窗口内复测。** RC1 的抑制窗口与漂移修复靠单测覆盖；
   下一个完整夜间窗口（23:58→10:02）才是最终验收。
6. **多显示器 / 非 936 代码页 / 英文版 Windows 未测。** 脚本按中文 Windows 编写，
   任务查询已改为不依赖文本猜测，但其它语言的错误串未实测。
7. **`qmctl.exe` 程序集版本仍是 1.0.0.0**，只有托盘是 2.0.0.0。不影响功能，
   但发布前应统一，否则「看版本判断装的是哪版」会误判。
8. **CI 未接入。** 测试目前靠 `Test-QuietMode.ps1` 在本机跑。

## 8. 复现步骤

```powershell
# 1. 构建
dotnet build QuietMode.sln -c Release

# 2. 单元测试
.\tests\QuietMode.Core.Tests\bin\Release\net481\QuietMode.Core.Tests.exe

# 3. 只读集成测试（含单元测试）
.\powershell\Test-QuietMode.ps1

# 4. 安装演练（不改系统状态）
.\powershell\Install-QuietMode.ps1 -Mode DryRun

# 5. 卸载演练
.\powershell\Uninstall-QuietMode.ps1

# 6. 体检
.\powershell\Repair-QuietMode.ps1

# 7. 特权目录自测
.\powershell\Test-Privilege.ps1
```
