# quiet-windows-toolkit

Windows 本机「深夜静音」工具集：23:58 起把风扇切到静音曲线、机械硬盘整盘脱机、
停掉会吵醒机器的维护任务；10:02 按安装时抓的 baseline 原样恢复。

不猜、不复述默认值：**白天恢复写的是安装那一刻实测到的原始状态**，不是代码里的默认值。

---

## 版本：v2 是正式版，v1 是历史兼容

| | v2（当前正式版） | v1（历史兼容，冻结） |
|---|---|---|
| 版本 | **2.0.5** | 不再演进 |
| 入口 | `qmctl.exe` | 仓库根 `quiet-hdd-*.ps1` |
| 安装器 | `powershell\Install-QuietMode.ps1` | 各脚本自己注册任务 |
| 调度 | 单一任务 `\QuietMode\QuietMode-Reconcile` | `\QuietHDD\` 两个闸门任务 |
| 配置 | `C:\ProgramData\QuietMode\config.json` | `%APPDATA%\QuietMode\settings.ini` + `diskgate.conf` |
| 认盘 | `uniqueId` + 序列号（编号永不参与） | 磁盘编号 `Disks=0,1` |

**新用户只用 v2**，从下面的[快速开始](#快速开始)第 1 步走起。

v1 的脚本和文档保留下来，只因为两件事：一是已有 v1 安装的机器需要它作为**迁移来源**和**回滚目标**；
二是 `docs/LEGACY_*.md` 这批审计资料引用了它。v1 的完整说明在 [`docs/LEGACY.md`](docs/LEGACY.md)。

> **v1 与 v2 不得同时作为独立调度器运行。**
> 两套闸门都去动同一块盘时会互相打架：v1 到点联机、v2 到点脱机，来回跳。
> 安装器在注册 v2 之后会停用 v1 闸门并**回读确认**；停不掉就把 v2 自己拆掉退回「只有 v1」，
> 绝不留两套并行调度。`-KeepLegacy` 是给调试场景留的逃生门，生产机不要用。

---

## 装之前必须知道它会动什么

[`SAFETY.md`](SAFETY.md) 列清了六类写面（计划任务 / `SysMain` 服务 / 自动维护注册表 /
`DISKIDLE` 电源设置 / 目标磁盘联机状态 / FanControl 当前配置）、三条失败语义
（默认演练、写后必回读、未提权不做一半）、五级退路和已知风险。
**没读这页不要执行 Apply。**

同时明确写死了绝不动的东西：Windows Update / Defender / 防火墙 / BitLocker / 证书服务，
以及系统盘 / 启动盘 / 页面文件所在盘 / 存储池成员 —— 这几类盘即使写进配置也会被执行前的
`DiskSafetyGuard` 硬拦。

---

## 目录

```
SAFETY.md                   会动什么 / 绝不动什么 / 出问题怎么退   ← 先读这页
QuietMode.sln               Core / Windows / Cli / 托盘 / 单测
src/
  QuietMode.Core/           配置、校验、迁移、快照、状态机、计划器、验证器（无系统调用）
  QuietMode.Windows/        WMI / 任务 COM / 注册表 / 电源 / 磁盘 / 调度注册（唯一系统层）
  QuietMode.Cli/            qmctl 命令行（机器侧唯一入口）
powershell/
  Install-QuietMode.ps1     安装器：DryRun / Apply / Rollback
  Uninstall-QuietMode.ps1   卸载（数据可访问性优先）
  Repair-QuietMode.ps1      只诊断；-Apply 才做三件低风险修复
  Reset-Baseline.ps1        重建 baseline
  Verify-NightRun.ps1       A01–A24 验收表
  Grant-TaskRead.ps1        任务可读性授权（文件夹 / 文件 / 任务对象三层）
  Fix-TaskVisibility.ps1    任务可见性修复
  Modules/                  8 个 .psm1（含 TaskState 判据模块）
  Worker/                   计划任务的唯一执行器
QuietMode/                  托盘：qmctl 的前端，不含任何系统调用
tests/
  QuietMode.Core.Tests/     C# 单测（自建 runner，无外部依赖）
  ps/                       Pester 5 测试
build/
  build.ps1                 编译整个解决方案并收集 qmctl 到 bin\
  run-pester.ps1            在当前 PowerShell 里跑全部 Pester（5.1 / 7.x 共用）
quiet-hdd-*.ps1             v1 遗留闸门脚本（历史兼容，勿与 v2 同时启用）
config/default-policy.json  默认策略（与代码里的 DefaultPolicy 同一份内容，测试会比对）
docs/                       架构 / 配置 / 调度 / 磁盘 / 维护 / 托盘 / v1 审计
```

---

## 快速开始

下面 13 步全部在**仓库根目录**执行。除第 9 步外都不需要管理员。

### 1. 系统要求

- Windows 10 1809+ 或 Windows 11，x64
- PowerShell **5.1**（系统自带）或 **7.7.0** —— 两条都支持，安装器在两个版本下各跑过一遍完整验收
- .NET Framework 4.8.1 运行时（`qmctl` 与托盘）
- .NET SDK 8（只在编译时需要）
- 管理员权限（Apply、磁盘脱机、计划任务、ACL 加固都要）
- FanControl（可选，走命名管道，不需要管理员）

### 2. 获取代码并编译

```powershell
git clone https://github.com/jmwangde3/quiet-windows-toolkit.git
cd quiet-windows-toolkit
dotnet build QuietMode.sln -c Release      # qmctl + Core + Windows + 托盘 + 单测
QuietMode\build\build.cmd                  # 单独产出托盘 QuietMode\QuietMode.exe
```

用发布包的话（`build\pack-release.ps1` 产出的 `QuietMode-v2.0.5-win-x64.zip`）解压后目录结构
已经满足 `qmctl` 的 `RepoLocator`（从自身目录向上找 `powershell\Modules`），不用再编译。

检查产物在不在：

```powershell
Test-Path .\src\QuietMode.Cli\bin\Release\net481\qmctl.exe   # 应为 True
Test-Path .\QuietMode\QuietMode.exe                          # 应为 True
```

### 3. 配置磁盘稳定身份

先看本机磁盘。**`UNIQUEID` 这一列就是 v2 认盘用的身份**，磁盘编号只是给人看的：

```powershell
.\bin\qmctl.exe disks list                # 人眼速览
.\bin\qmctl.exe --json disks list         # 要抄准确值就用这个
```

生成一份默认配置，然后编辑 `C:\ProgramData\QuietMode\config.json` 的 `disks.targets[]`：

```powershell
.\bin\qmctl.exe config init
notepad C:\ProgramData\QuietMode\config.json
```

```json
"disks": {
  "enabled": true,
  "forceBusy": false,
  "targets": [
    {
      "uniqueId": "<从 --json disks list 的输出里抄 uniqueId>",
      "serialNumber": "<可选，抄 serialNumber>",
      "friendlyName": "<可选，抄 friendlyName>",
      "sizeBytes": 0,
      "busType": "<可选，抄 busType>",
      "enabled": true
    }
  ]
}
```

要点：

- `uniqueId` 与 `serialNumber` **至少填一个**，两个都空会被 `config validate` 直接判错；
  两个都填时按 `uniqueId` 优先匹配，序列号兜底。
- **磁盘编号（DISK 0 / DISK 1）永远不参与匹配**。换 SATA 口、加一块盘导致编号漂移，都不会脱错盘。
- `config set` 的白名单里**没有** `disks.targets` —— 它是个数组，键值对表达不了，
  只能改文件。改完必须重新跑第 6 步的校验。
- `sizeBytes` 留 0 只是少一层核对，会给一条警告，不影响运行。
- 不需要脱机就把 `"enabled"` 设成 `false`，别留个 `true` 加空数组，那会直接校验失败。

### 4. 配置夜间和白天时间

```powershell
.\bin\qmctl.exe config set schedule.nightStart 23:58
.\bin\qmctl.exe config set schedule.dayStart   10:02
.\bin\qmctl.exe config set schedule.reconcileIntervalMinutes 10
.\bin\qmctl.exe config set schedule.startupDayStart 06:00
```

开机补偿：开机时长小于 `schedule.startupGraceMinutes`（默认 15 分钟）且当前已过
`startupDayStart`（06:00）时按白天处理 —— 早上开机直接联机，00:00–05:59 的无人值守重启
仍保持夜间状态。

`config set` 每次写完都会重新校验一遍，**校验不过就不落盘**。写完看一眼当前全貌：

```powershell
.\bin\qmctl.exe config show
```

### 5. 读一遍 SAFETY.md

```powershell
notepad .\SAFETY.md
```

重点看「写面清单」和「五级退路」两节。这一步不产生任何系统改动，但跳过它的代价是
第 13 步出问题时不知道该往哪退。

### 6. 校验配置

```powershell
.\bin\qmctl.exe config validate
echo $LASTEXITCODE      # 必须是 0
```

常见不通过的原因：`disks.enabled=true` 但 `targets` 为空；`uniqueId` 与其他目标重复；
`fan.enabled=true` 但 `ipcPath` / `silentProfile` 指向的文件不存在。

### 7. 执行安装 DryRun

```powershell
powershell -ExecutionPolicy Bypass -File .\powershell\Install-QuietMode.ps1 -Mode DryRun
```

**这一步不需要管理员，且只读**：不建目录、不写日志、不动任何系统状态。
它跑七段预检：环境 → 载体完整性（二进制 + 8 个模块 + Worker 逐个校验大小/版本/sha256）→
现有安装 → 磁盘稳定身份 → 静音窗口与 FanControl 依赖 → baseline 可信度 → 完整安装计划。

### 8. 检查 DryRun 结果

退出码 0 才往下走。三个必须亲眼看的地方：

- `[03] 现有安装`：确认它认出了你机器上真实存在的东西（v1 三份配置、v1 四条历史任务、v2 任务）。
  `不存在` 是正常的。
- `[05] 磁盘稳定身份`：目标盘必须是 `matched` 且有 `uniqueId`。任何一块盘 `ambiguous` 或
  `notfound` 都先查清再装。
- `[07] 安装计划`：这是它**准备做的事**的完整清单，逐条读完。

有 FAIL 就停下改配置，别指望 Apply 阶段「也许能过」。

### 9. 管理员执行 Apply

右键「以管理员身份运行」Windows PowerShell，`cd` 到仓库根：

```powershell
powershell -ExecutionPolicy Bypass -File .\powershell\Install-QuietMode.ps1 -Mode Apply
```

二十步：备份 → 部署 → 迁移 → 校验 → 抓 baseline → 权限加固 → 注册任务 → 停用 v1 →
交回自动 → 对齐一次 → 起托盘 → 收尾复核。日志落在 `C:\ProgramData\QuietMode\logs\install-<时间戳>.log`。

退出码含义：

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 2 | 有失败项（逐条看 `[FAIL]` 行） |
| 3 | 预检或自检失败（含任务状态判据自检） |
| 4 | 备份 / 配置 / 快照失败 |
| 5 | 未提权 |
| 6 | v2 任务没注册上 |
| 7 | v1 停不掉，已把 v2 回滚掉 |

### 10. 验证任务注册

```powershell
.\bin\qmctl.exe task status
Get-ScheduledTask -TaskPath '\QuietMode\' | Format-Table TaskName, State
```

要看到的：`\QuietMode\QuietMode-Reconcile` 存在、`State = Ready`、
`NextRunTime` 是下一个 23:58 或 10:02。

> `LastTaskResult = 267011`（`0x41303`）意思是「**从未运行**」，不是错误。
> 刚装完就是这个值，等第一次到点跑过才会变。

### 11. 验证权限

任务由 SYSTEM 注册，默认普通用户看不见 —— 托盘就永远显示「状态未知」。三层都要授权：

```powershell
# 只检查，不改（退出码 0=已就绪 / 2=有缺失 / 3=未提权没执行）
powershell -ExecutionPolicy Bypass -File .\powershell\Grant-TaskRead.ps1 -Check

# 任务可见性诊断（不加 -Apply 就是只读）
powershell -ExecutionPolicy Bypass -File .\powershell\Fix-TaskVisibility.ps1
```

然后**开一个不提权的窗口**验证普通用户确实能读到：

```powershell
.\bin\qmctl.exe task status        # exists 应为 true，不是 null
```

判据在 `powershell\Modules\QuietMode.TaskState.psm1` 里，只看「有没有 BU 这条 ACE」是不够的 ——
`(A;OICI;FW;;;BU)`（只写）也会被当成「有 ACE」，所以必须看权限位里有没有读。

### 12. 验证夜间和白天行为

```powershell
.\bin\qmctl.exe status                       # 窗口 / 决策 / 风扇 / 磁盘 / 停转 / 任务一次看全
.\bin\qmctl.exe reconcile auto               # 演练：只算不动
.\bin\qmctl.exe reconcile auto --apply --trigger manual   # 真对齐一次（要管理员）
```

跑完整验收表（A01–A24，含夜间与白天两条链）：

```powershell
powershell -ExecutionPolicy Bypass -File .\powershell\Verify-NightRun.ps1 -Qmctl .\bin\qmctl.exe
```

`-Qmctl` 的默认值是作者本机的部署路径，**在源码仓库里跑必须显式传**。
输出是 `PASS / FAIL / MANUAL` 三态表；`MANUAL` 是「得等到夜间或需要人看」的项，不是失败。

等第一夜过去后复跑一次，A01–A24 应该只剩个别 `MANUAL`。

### 13. 故障恢复

按代价从小到大：

| 级别 | 做什么 | 命令 |
|---|---|---|
| 1 | 手动开 / 关静音 | `qmctl apply manual-quiet --apply` / `manual-normal --apply` |
| 2 | 今晚别自动开 | `qmctl apply manual-normal --apply`（抑制当前夜间窗口） |
| 3 | 体检 | `Repair-QuietMode.ps1`（只诊断）/ 加 `-Apply` 做三件低风险修复 |
| 4 | 重建 baseline | `Reset-Baseline.ps1 -Apply`（会重抓；夜间抓会留「原始状态未知」说明） |
| 5 | 卸载 | `Uninstall-QuietMode.ps1 -Mode DryRun` → `-Mode Apply` |
| 6 | 退回 v1 | `Install-QuietMode.ps1 -Mode Rollback -DryRun` → 去掉 `-DryRun` |

`Uninstall-QuietMode.ps1` **不删** `baseline` / `recovery` / `logs` —— 那是失败时唯一的退路。
`-DeployDir` 默认值指向仓库里的 `QuietMode\`，部署到别处时必须显式传。

---

## 日常操作

```powershell
qmctl status                      # 一次调用给出全部事实
qmctl state show                  # 只读状态机
qmctl spin show                   # DISKIDLE 当前值
qmctl disks verify                # 目标盘联机状态与身份复核
qmctl task status                 # 任务 + 最后一条心跳 + 距今分钟数
qmctl maint status                # 维护任务与 SysMain 观测
```

静音与「硬盘空闲停转」是**两个独立开关**，互不绑定：`spin.enabled=false` 时整个
`spindown` 步骤标 `skipped`，一个字节都不写。

调度暂停与手动静音都只是**记状态**，真正的动作永远由下一次 reconcile 完成 ——
「谁决定」和「谁执行」不分裂成两套逻辑。

---

## 兼容性

| 项 | 值 |
|---|---|
| PowerShell 5.1 (Desktop) | 通过 |
| PowerShell 7.7.0 (pwsh) | 通过 |
| Windows 10 / 11 x64 | 通过 |
| .NET Framework 4.8.1 | 必需 |

CI 矩阵：`windows × PowerShell 5.1`、`windows × PowerShell 7.7.0`（官方 zip 精确安装，不用 runner
预装的 pwsh，也不用 `latest`）、`ubuntu × Core 编译`。任何一条腿失败都不会被 `continue-on-error`
放行。Pester 版本也钉死（`-RequiredVersion 5.7.1`）—— 测试框架版本漂移会让「一边绿一边红」
变成无法解释的事。

测试规模：C# 单测 **123** 项；Pester **186** 条（本机 182 通过 / 4 跳过）。跳过的是
`Grant-TaskRead` 里那 4 条真实任务 ACL 集成用例 —— 它们要设 `QM_ALLOW_TASK_INTEGRATION=1`
**并且**在管理员会话里才跑。本机是日常主机，默认不打开这个开关；CI 的 `windows-latest`
runner 本身提权，工作流里已经打开，所以那 4 条在两条 Windows 腿上是真跑的。

### ACL 真机验收

真实写任务对象的 SDDL、改 `System32\Tasks` 下的文件 ACL，这层单独有一个入口：

```powershell
# 默认只读预检，十项前置条件逐条判
.\powershell\Test-QuietModeAclApply.ps1 -Preflight

# 管理员 PowerShell 7.7.0 窗口
.\powershell\Test-QuietModeAclApply.ps1 -Apply      # 建夹具 → 调生产实现 → 回读 → 幂等 → Check → 比对生产
.\powershell\Test-QuietModeAclApply.ps1 -Check      # 只读：前后快照比对，证明不改状态
.\powershell\Test-QuietModeAclApply.ps1 -Rollback   # 按原始 SDDL 逐项恢复并回读校验
.\powershell\Test-QuietModeAclApply.ps1 -Cleanup    # 注销任务、删文件夹、归档报告
```

测试对象是 `\QuietMode-ACL-Test-<GUID>\QuietMode-ACL-Probe`：Disabled、无触发器、
动作只有 `cmd.exe /c exit 0`，不引用任何真实资源。授权动作全部发生在生产脚本
`Grant-TaskRead.ps1` 里（子进程调用，参数与安装器 `[15]` 步一致），验收脚本自己不复制一份实现。
生产保护是运行时代码：路径必须严格等于当前 GUID 的测试文件夹，命中 `\QuietMode` / `\QuietHDD` /
`\Microsoft\Windows` 任一前缀立即中止。

本机实测记录见 [`docs/ACL_APPLY_HOST_VALIDATION.md`](docs/ACL_APPLY_HOST_VALIDATION.md)。

---

## 开发

```powershell
.\build\build.ps1                                  # 编译 + 收集 qmctl 到 bin\
.\build\run-pester.ps1                             # 当前 PowerShell 里跑全部 Pester
.\build\run-pester.ps1 -NUnitXml testResults.xml    # 顺带出 NUnit XML（CI 用）
.\powershell\Test-QuietMode.ps1                     # 只读自检
```

Pester 5 的 session state 会复制变量：`SecurityIdentifier` **对象**传进 `It` 之后会失效，
`AddAccessRule` 静默失败、`Access.Count` 变成 0。写 ACL 测试时一律在函数内部现取 SID，
断言用 SDDL 字符串，别断言 `Access.Count`。

含 `exit` 的脚本被 `& script.ps1` 直接调用会把宿主一起结束，测试里必须走子进程
（`(Get-Process -Id $PID).Path -File`）。

---

## Windows 中文编码约定

本机 PowerShell 5.1 Desktop + zh-CN / 代码页 936(GBK)，未开系统级 UTF-8；Bash 侧 UTF-8 正常。

- 优先 Bash；必须用 PowerShell 时开头写
  `$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)`。
- 读文件用 `Get-Content -Raw -Encoding UTF8`；写文件用
  `[System.IO.File]::WriteAllText($p,$s,(New-Object System.Text.UTF8Encoding($false)))`。
- 含中文的 `.ps1` 存 **UTF-8 带 BOM**（不带 BOM 时 PS 5.1 按 936 解码，中文和正则一起坏）；
  `.bat/.cmd` 存 GBK(936) **无** BOM。`tests/ps/Install-Contract.Tests.ps1` 会检查这两条。
- 中文路径一律加引号并用 `-LiteralPath`。

---

## 文档

| 文档 | 内容 |
|---|---|
| [`SAFETY.md`](SAFETY.md) | 写面清单 / 禁改范围 / 失败语义 / 五级退路 / 已知风险 |
| [`docs/LEGACY.md`](docs/LEGACY.md) | v1 历史实现与迁移说明 |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | 分层与数据流 |
| [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) | 配置模型与迁移 |
| [`docs/SCHEDULING.md`](docs/SCHEDULING.md) | 状态机、窗口、reconcile 链 |
| [`docs/DISK_MANAGEMENT.md`](docs/DISK_MANAGEMENT.md) | 磁盘身份与安全闸 |
| [`docs/MAINTENANCE.md`](docs/MAINTENANCE.md) | 维护任务清单与恢复 |
| [`docs/TRAY.md`](docs/TRAY.md) | 托盘与 qmctl 的契约 |
| [`docs/RC_BASELINE.md`](docs/RC_BASELINE.md) | 验收基线 |
| [`CHANGELOG.md`](CHANGELOG.md) | 变更记录 |

## 许可

MIT，见 `LICENSE`。
