# 变更记录

## 2.0.5 — 2026-11-15

主题：**安装器跨版本兼容性与发布工程加固。核心行为零改动** —— 夜间静音、维护任务禁用、
`SysMain` 夜间策略、整盘 Offline、FanControl 静音、白天按 baseline 恢复、单一 v2 调度器、
状态机与 10 分钟对账、安全边界与回滚不变量，全部保持原样。本版没有新增静音模式、没有新调度功能、
没有扩大禁用任务范围。

### 1. 任务状态判据抽成模块，一份实现三个脚本共用

新增 `powershell/Modules/QuietMode.TaskState.psm1`。导出 `Split-QmTaskPath`、`Test-QmAdmin`、
`Test-QmUsersReadAce`、`Test-QmFileHasUsersRead`、`Resolve-QmTaskQueryOutcome`、`Get-QmTaskState`。
`Install-QuietMode.ps1` / `Grant-TaskRead.ps1` / `Fix-TaskVisibility.ps1` 改为薄包装，调用点未动。

判据收紧成三态：

- `Exists = $true` —— 确实读到了任务；
- `Exists = $false` —— **只有明确证明任务不存在**（FQID `CmdletizationQuery_NotFound`，或命中文末的 not-found 白名单文本）；
- `Exists = $null` —— 其余一律「无法确认」：拒绝访问、RPC 不可用、服务未启动、名称非法、未知异常。

**未提权时即使命中了 not-found 文本，也只返回 `$null`** —— 文件夹 ACL 的访问检查发生在任务对象之前，
「看不见」不等于「不存在」。2.0.4 遗留的 `-match '找不到|不存在'` 宽泛正则因此被彻底移除：
`Access Denied`、`RPC 服务器不可用` 只要带上这几个字就会被误判成「任务不存在」，
而在单调度不变量里，误判的代价是整台机器失去调度器。

### 2. 安装器自检从一层变两层

`[03] 现有安装` 原先只做环境自检（探一个不存在的任务）。现在先跑**纯判据自检**：

- 喂 PS 5.1 措辞、PS 7.7.0 措辞、英文措辞三种 not-found → 必须判 `$false`；
- 喂 PermissionDenied / ResourceUnavailable / 找不到网络路径 / 未知异常 → 必须判 `$null`；
- 喂 `FW`（只写）SDDL → 必须判「没有 Users 读权限」。

任一条不符直接 `exit 3`，不进入任何写操作。再跑环境自检（提权下必须 `$false`，未提权下必须 `$null`）。

这层自检不是装饰：开发过程中我自己写的 not-found 正则英文分支少写了一次对象名，
就是被它当场抓出来的（`FAIL 自检失败：任务状态判据与预期不符 / not-found 措辞没判成 false`）。

### 3. 授权段补上退出码判据

`[15]` 原先只看 `Grant-TaskRead.ps1` 的输出文本里有没有失败汇总行。现在先看退出码：

```
$grantCode = $LASTEXITCODE
if ($grantCode -ne 0) { Write-Bad ('授权脚本退出码 ' + $grantCode + '（2=有授权失败，3=未提权未执行）') }
elseif ($text -match '(?m)^\s*结果：.*失败') { Write-Bad '授权脚本退出码 0 但输出里有失败汇总行' }
```

### 4. `Grant-TaskRead.ps1` 三处缺陷

| 缺陷 | 后果 | 修法 |
|---|---|---|
| `Test-UsersReadAce` 只看「有没有 BU 这条 ACE」 | `(A;OICI;FW;;;BU)`（只写）被当成已有读，直接 `continue` 跳过 | 看权限位：别名 `FR/FA/GR/GRGX/GA` 含读，`FW/FX/SD/WD` 不含；十六进制查 `FILE_READ_DATA(0x1)` 与 `GENERIC_READ(0x80000000)` |
| `Grant-TaskFileRead` 写后不回读 | `Set-Acl` 不抛异常就 `Added++`，实际可能没写进去 | `Set-Acl` 后重新 `Get-Acl` 并复判，读不到就 `throw` |
| 未提权跑 `-Apply` 返回 `exit 0` | 调用方以为授权已经做完 | 改 `exit 3` |

任务对象授权那两处 `-match '\(A;[^()]*;;;BU\)'` 一并换成 `Test-UsersReadAce -Sddl`。

另加 `-TaskNames` 白名单：给了就只对点名的任务调用 `GetTask`，**不枚举**；没给保持原来的
全枚举。安装器调用的仍然是不带该参数的形态，正式安装的默认目标范围一个字节都没变。
这条参数是给下面的 ACL 真机验收用的 —— 「只碰我点的那一个对象」和「碰巧枚举到一批」
在 ACL 写入里是两件不同的事。

### 5. 安装器支持发布包扁平布局

`$cliBin` / `$trayBin` 改为按候选列表探测：源码编译产物优先，找不到再回退到包根
（`qmctl.exe` 与 `powershell\` 同级）。**仓库里编译过的情况下路径一个字都不变**；
解析结果打印在头部 `二进制` / `托盘` 两行，出问题一眼能看出用的是哪份载体。

这条不做的话，解压出来的发布包跑 Apply 会死在载体完整性检查上，报错还是「先 dotnet build」——
对着一份没有源码的包说这句话没有意义。

### 6. 非提权回滚演练重新可用

`-Mode Rollback -DryRun` 原先被权限门拦下（非提权一律 `exit 5`）。回滚演练只打印动作、
不删任务、不动闸门、不还原二进制，没有理由要求提权。权限门因此放行这一个组合。

回滚段 `$s.Exists -eq $null` 由 `Write-Bad`（进失败清单）改为 `Write-Warn2` ——
「读不到」不等于「删不掉」，不该让一次无法确认把整次回滚判成失败。

### 7. CI 矩阵

`.github/workflows/ci.yml` 从单 job 扩成三条腿：

| 腿 | 跑什么 |
|---|---|
| `windows-latest` × PowerShell **5.1** | 编译 + C# 单测 + 全部 Pester |
| `windows-latest` × PowerShell **7.7.0** | 同上（官方 zip 精确安装，不用 runner 预装的 pwsh，不用 `latest`） |
| `ubuntu-latest` × Core 编译 | 只编 `QuietMode.Core`（按设计无系统调用），不跑测试 |

每条腿都先断言解释器版本（5.1 用 `5.1.*` 前缀，7.7.0 用精确匹配），版本不符直接失败 ——
锁版本这件事必须能自证，否则 runner 镜像一换就静默失去覆盖。
`fail-fast: false` 只是为了让两条腿都跑完方便对比，**没有任何 `continue-on-error`**。

首跑两条腿都红了。修完前两个又红了一次，第三个才是真问题。四件事：

1. **7.7.0 腿下载 msi 拿到 404。** v7.7.0 的发布资产里只有
   `PowerShell-7.7.0-win-x64.zip` / `.msixbundle`，没有 `.msi`。
   改成取官方 zip，先按体积（< 20MB 判为错误页）和 zip 条目（必须含 `pwsh.exe`）
   两道探测确认是真包，再解压、再找 `pwsh.exe`（不假设它在根目录）。
2. **5.1 腿把一次成功的调用判成失败。** `Verify qmctl artifact` 里写的是
   `$first = (& $tool --help | Select-Object -First 1)` 然后看 `$LASTEXITCODE`。
   早停管道会掐断原生进程，退出码再也不会被记录：pwsh 7 下是 `$null`，
   PS 5.1 下是被杀进程的非零残值，两条路都撞上 `-ne 0`。
   改成先收全部输出、再看退出码、最后取首行。`release.yml` 的同款写法一并改掉。
3. **一条测试写死了开发者机器。** `TaskState` 的真机用例断言
   「`\QuietMode\QuietMode-Reconcile` 若存在必须读到 `Exists=true`」，
   而 runner 上根本没装 v2 —— `false` 才是正确答案，两条腿都红在它上面。
   改成拿「枚举任务后按路径+名字找」当参照物：只回答在不在，不碰异常文本，
   所以不会跟被测判据同源出错。三种情况各断言一条：
   探针看得到 → 不许判 `false`；提权且探针看不到 → 必须确定为 `false`；
   未提权 → 必须 `null`（看不见 ≠ 不存在）。
4. **Pester 版本漂移。** `-MinimumVersion 5.7.0` 在 runner 上装到了 **6.2.0**，
   开发机是 **5.7.1**。测试框架大版本不同，一边绿一边红就没法互相印证，
   等于白跑一遍矩阵。改成 `-RequiredVersion 5.7.1` 精确安装，装完回读断言；
   `release.yml` 用同一个字面量，并用契约测试强制两边一致。

### 8. 可复现发布包

新增 `build/pack-release.ps1` → `QuietMode-v2.0.5-win-x64.zip` + `SHA256SUMS.txt`。
「可复现」= 内容清单固定 + 条目顺序固定（按相对路径序数排序）+ 条目时间戳固定（常量 `2000-01-01`）。
实测同一份输入连打两次 SHA256 完全一致：

```
64da1b4005616e7fa5b1b5b317980e09f95e396c497e6ff9fa690e08d7ca7e91
376,995 B / 51 个条目
```

包里含：`qmctl.exe`、`QuietMode.exe`、两个 DLL、两份 `.config`、`powershell\` 全树
（17 个脚本 + 8 个模块 + Worker + 2 个 `.cmd`）、`config/default-policy.json`、
`README.md`、`SAFETY.md`、`LICENSE`、`docs\*.md`、`SHA256SUMS.txt`。
不含 `tests\`、`obj\`、`.git\`、`*.pdb`。

**`CHANGELOG.md` 故意不进包。** 它在 2.0.5 这一节里记着这个 zip 的 SHA256，
而它自己又在 zip 里 —— 一旦打进去就构成自指：包内那份记录的必然是上一代的哈希，
每次重打都对不上，永远自洽不了。打包脚本为此加了一条硬断言，谁把它加回来就直接
`exit 3`。验证方式也留了：改完 CHANGELOG 再打包，哈希必须保持不变
（实测改前改后同为 `f7307683…`）。

脚本会断言三件事：契约文件齐全（含 C# 控制器按常量算出的三条脚本路径）、
包根含 `powershell\Modules`（`qmctl` 的 RepoLocator 要求）、包里没有测试/临时产物。

### 9. Release 工作流

新增 `.github/workflows/release.yml`，只响应 `v*.*.*` tag。`preflight` job 用严格正则
`^v\d+\.\d+\.\d+$` 判定，预发布 tag（`v1.2.3-rc.1`）判为 `is_release=false`，
`publish` job 直接跳过 —— 让预发布 tag **安静跳过**而不是红着失败。

`publish` 在发版前依次卡：编译 → C# 单测 → Pester 全绿 → 打包 → 包名与 tag 一致 →
包内 `qmctl --help` 版本与 tag 一致 → `SHA256SUMS.txt` 逐条自校验 → 包内无测试产物。
任何一步不过都不发版。

**本版只验证工作流，不建 tag、不发 Release。**

### 10. 版本号统一 2.0.5

- `QuietModeConfig.Version` → `2.0.5`（产品版本唯一来源）；
- `QuietMode.csproj` / `QuietMode.Cli.csproj` / `QuietMode.Core.csproj` / `QuietMode.Windows.csproj`
  的 `<FileVersion>2.0.5.0</FileVersion>` / `<ProductVersion>` / `<InformationalVersion>` 同步；
- `<AssemblyVersion>` **保持 `2.0.0.0` 不动** —— 程序集标识不该跟产品版本走。

后三个 csproj 原先完全没有版本声明，导致安装器 `[02]` 一直显示 `v1.0.0.0`。

### 11. 文档

- **README 重写**：首页第一屏讲清「v2 是正式版、v1 是历史兼容、两者不得同时作为独立调度器运行」；
  快速开始改为 13 步 v2 流程，所有命令按真实 CLI 语法核对过（含 `config set` 白名单里
  **没有** `disks.targets`、必须手改 JSON 这个事实）。删掉指向已不存在的 `QuietMode/QuietMode.cs` 的引用。
- 新增 `docs/LEGACY.md`：v1 的组件清单、真实命令、行为细节、迁移与回退、10 项结构性缺陷、本机遗留状态。
- `docs/LEGACY_AUDIT.md` 与 `docs/LEGACY_BEHAVIOR.md` 顶部加「历史资料」标注，指回 README 与 LEGACY.md。

### 12. 测试

| 文件 | 用例数 | 内容 |
|---|---|---|
| `tests/ps/QuietMode.TaskState.Tests.ps1` | 38 | 路径拆分、不存在 6 例、无法确认 10 例、SDDL 判据 10 例、是否提权、真机只读 4 例 |
| `tests/ps/Grant-TaskRead.Tests.ps1` | 20 | 内存 ACL 判据 9 例、SDDL 与 ACL 一致性 4 组、脚本契约 5 例（子进程取退出码）、真实任务集成 4 例（默认跳过） |
| `tests/ps/Test-QuietModeAclApply.Tests.ps1` | 23 | ACL 真机验收入口的静态契约（不自行加 ACE、生产保护、夹具形态、恢复根位置）、模式互斥与默认只读、非法 TestId 拦截、未提权三个写模式必须 `exit 3`、`-TaskNames` 精确限定 |
| `tests/ps/Install-Contract.Tests.ps1` | 25 | 全目录 PS1/PSM1 双版本 Parser 语法、BOM/换行约定、版本号层次、安装器契约、DryRun 与回滚演练子进程验证；其中 7 条是 CI 工作流契约（双版本矩阵、无 `continue-on-error`、PS 7 走 zip 不走 msi、`--help` 不得接早停管道、tag 逐段判定、不自己打 tag、两个工作流钉同一个 Pester 版本） |

原有 8 个测试文件 79 条**一条没删**。合计 **185 条**：
PowerShell 5.1 `Total=185 Passed=181 Failed=0 Skipped=4`，
PowerShell 7.7.0 同一组数字。4 条跳过的是需要提权的真实任务 ACL 集成用例
（`QM_ALLOW_TASK_INTEGRATION=1` 才跑）。C# 单测 **123 项**全部通过。

### 13. 其它

- 新增 `build/run-pester.ps1`：双版本 Pester 运行器，`-NUnitXml <path>` 一次运行同时断言并产出报告
  （不为了拿 XML 再跑第二遍）；零测试 `exit 2`、有失败 `exit 1`。
- `.gitignore` 增加 `dist/`。

### 14. ACL 真机验收入口

新增 `powershell/Test-QuietModeAclApply.ps1`。它回答的是本版最要紧的那个未决问题：
**PowerShell 7.7.0 下真实写任务 ACL 到底行不行** —— 而这件事此前只能靠推断，
因为它要求动真实对象，日常主机上不合适。

做法是把风险面缩到最小：建一个一次性测试任务 `\QuietMode-ACL-Test-<GUID>\QuietMode-ACL-Probe`，
Disabled、无触发器、动作是 `cmd.exe /c exit 0`，**不引用任何真实资源**；然后用生产脚本
`Grant-TaskRead.ps1`（安装器 `[15]` 步调用的同一份）去授权它，再从三个角度回读取证。

| 模式 | 做什么 | 默认 |
|---|---|---|
| `-Preflight` | 十项前置条件逐条判，任一不过 `exit 3` | 是（不带开关即此模式） |
| `-Apply` | 建夹具 → 存原始 SDDL → 调生产脚本 → 回读 → 幂等复跑 → Check 只读 → 比对生产 | |
| `-Check` | 前后三次快照比对，证明「检查」本身不改状态 | |
| `-Rollback` | 按原始 SDDL 逐项恢复并回读校验 | |
| `-Cleanup` | 注销任务、删文件夹、核对生产未变、归档报告 | |

几条硬约束写成了运行时代码而不是注释：

- **生产保护**：每次操作任务对象前过 `Assert-TestScoped` —— 路径必须严格等于当前 GUID
  的测试文件夹，命中 `\Microsoft\Windows`、`\QuietHDD`、`\QuietMode` 任一前缀立即 `throw`；
  `TestId` 必须是标准 GUID（它会被拼进文件夹名）。
- **不复制实现**：本脚本一行 `AddAccessRule` 都没有，授权全部发生在子进程调用的生产脚本里。
  复制一份进测试脚本，测的就是复制品，没意义。
- **基线只存 DACL**：`GetSecurityDescriptor(0xF)` 读到的串含 SACL，拿它回写需要
  `SeSecurityPrivilege` —— 存了个自己写不回去的基线等于没有基线。0xF 只作证据记录。
- **写后必回读**：`Set-Acl` 不抛异常 ≠ ACE 落到文件上；`SetSecurityDescriptor` 之后
  原对象作废，必须重新 `GetTask` 再读。
- **恢复资料先落盘再写 ACL**：`state.json` 在第一次写之前就在
  `%ProgramData%\QuietMode-AclTestRecovery\<GUID>\` 里了。放这个独立目录而不是生产配置
  目录，是因为后者被安装器权限加固过（Users 只读）——非提权 `-Preflight` 写不进、
  提权 `-Apply` 写进去、之后非提权 `-Rollback` 又读不回来，恢复通道不能依赖「你现在是不是管理员」。
- **失败不删证据**：恢复失败时恢复目录原样保留，报告里给出原始 SDDL，可以手工复原。

真机执行结果见 `docs/ACL_APPLY_HOST_VALIDATION.md`。**本版未在本机跑通完整 `-Apply`**
（当前会话非提权），该文档如实标注了执行到哪一步、哪一步需要管理员窗口重跑。

## 2.0.4 — 2026-11-15

### 严重：PowerShell 7 下 Apply 会把自己装的 v2 回滚掉

真机事故。用 **PowerShell 7.7.0** 跑 `-Mode Apply`，20 步走到第 16 步突然回滚：

```
有 2 个 v1 闸门停不掉：\QuietHDD_NightGate, \QuietHDD_DiskGate
为避免两套调度器同时控制磁盘，回滚刚注册的 v2 任务。
     OK   v2 任务已移除，回到只有 v1 的状态
```

结果：v2 任务被删，机器上**没有任何调度器**，当晚不会自动进夜。

### 根因：一句话的差别让判据静默失效

`Get-TaskState` 靠异常文本区分「任务不存在」和「读不到」：

```powershell
if ($msg -match 'No MSFT_ScheduledTask|找不到任何匹配|找不到|does not exist|不存在') {
```

两个 shell 的措辞差一个字：

| 环境 | 异常文本 | 旧正则 |
|---|---|---|
| Windows PowerShell 5.1 | **找不到任何匹配**的 MSFT_ScheduledTask 对象 | 命中 |
| PowerShell 7.7.0 | **未找到匹配**的 MSFT_ScheduledTask 对象 | **漏** |

PS 7 下匹配不上 → `Exists` 落到 `$null`（无法确认）→ 第 7 步把它算进
`$legacyStillOn` → 触发不变量回滚。

触发点是 `$script:LegacyTasks` 里的**历史兼容条目**：v1 装过两种路径（带 `\QuietHDD\`
文件夹的、直接挂根下的），所以四个都查。根路径那两条本来就该「不存在」，在 PS 5.1 下
安静跳过，在 PS 7 下被误判成「停不掉」。

而真正在用的那两个 `\QuietHDD\` 任务，第 16 步明明已经 `OK 已禁用` 了。

### 三处修法

**1. 判据加 FQID，文本只兜底**

`CmdletizationQuery_NotFound` 这个 FQID 在 PS 5.1 和 7 下**完全一致**，靠它比靠措辞可靠：

```powershell
$notFound = [bool]($_.FullyQualifiedErrorId -match 'CmdletizationQuery_NotFound')
if (-not $notFound) { $notFound = ($msg -match '...|未找到匹配|找不到任何匹配|...') }
```

**2. 第 3 步加常驻自检，判错就退出而不是毁安装**

Apply 之前先探一次不存在的任务（`\__QuietModeSelfCheck__\__NoSuchTask__`），
`Exists` 不是 `$false` 就直接 `exit 3` 并提示换 5.1。这个自检是运行时真实验证，
比单测更能拦住环境差异。

**3. `Grant-TaskRead.ps1` 的 PS 7 兼容**

`$fileInfo.GetAccessControl()` / `SetAccessControl()` 在 .NET Core 里被挪进了静态类
`FileSystemAclExtensions`，实例上根本没有 —— 第 15 步因此刷了一屏
`[System.IO.FileInfo] 不包含名为 'GetAccessControl' 的方法`。改用 `Get-Acl` / `Set-Acl`，
两个版本语义一致。顺带把 `结果：N 个文件夹失败` 改成 `N 处授权失败（文件夹或任务文件）`。

### 实测证据

PowerShell 7.7.0 探针：

```
FQID=CmdletizationQuery_NotFound,Get-ScheduledTask
MSG=... 未找到匹配的 MSFT_ScheduledTask 对象 ...
MATCH_FQID=True     ← 新判据命中
MATCH_NEW=True      ← 补全文本命中
MATCH_OLD=False     ← 旧判据漏，这就是 bug
```

Windows PowerShell 5.1 同一探针：`MATCH_OLD=True`、`MATCH_FQID=True` —— 老环境不受影响。

修复后在 PS 7 下跑 DryRun，第 3 步 `OK 任务查询语义自检通过`，原先那两条
`WARN v1 任务 \QuietHDD_NightGate 无法确认` 消失。

## 2.0.3 — 2026-11-15

### 修复：`lastBootReconcileUtc` 是死字段，开机补跑无法自证

`QuietModeState` 里这个字段从 schema 5 起就存在 —— 声明了、序列化了、读取了，
**但全代码库没有一处写入**。后果是 `state.json` 里它恒为 `""`：任务带着
`<BootTrigger>`，重启后到底补跑没补跑，从状态文件里看不出来。

验收项 A09 因此只能给到「任务 XML 含 BootTrigger（需真重启验证）」这种半吊子结论 ——
它证明的是「任务被配置成开机跑」，不是「开机真的跑了」。

改法：`StateMachine.ApplyRun` 增加 `trigger` 参数（默认 `null`，不破坏既有调用），
只有 `trigger=boot` 才落盘这个时间戳：

```csharp
if (string.Equals(trigger, "boot", StringComparison.OrdinalIgnoreCase))
{
    st.LastBootReconcileUtc = stamp;
}
```

刻意**不**让 `schedule` 心跳覆盖它 —— 这个字段的语义是「最近一次开机补跑发生在何时」，
不是「最近一次心跳」。被心跳冲掉就失去意义了。

`ReconcileController` 把 `run.Trigger` 传进去，触发名来自任务命令行 `--trigger boot`。

单测 122 → **123**（4 个断言：新状态为空、调度不写、boot 写入、后续心跳不覆盖、
大小写不敏感）。Pester 79/79 仍全绿。

## 2.0.2 — 2026-11-15

### 修复：验收脚本把「机器状态正确」误判成 FAIL

本版**只改 `powershell/Verify-NightRun.ps1`，不含任何二进制改动**。因此 `qmctl --help` 仍显示
`2.0.1` —— 那反映的是二进制版本，与实际部署的构建一致。2.0.2 是发布序列号。

两处判定口径缺陷，命中时都会报出「机器有问题」的假警报：

**A21「重复执行不报错（心跳全 Ok）」**

原先把 `reconcile.log` 里**所有触发方式**的非 Ok 记录都算成心跳故障。实际 2026-11-15 那次
24 小时窗口里的 4 条非 Ok 全部来自人工操作：

| 时刻（本地） | 触发 | 真相 |
|---|---|---|
| 01:14:44 | `manual day` | 2.0.1 修复前的相位 bug 现场 |
| 01:15:03 | `manual day` | 同上，`NightDegraded` |
| 01:30:28 | `manual night` `apply=0` | 演练，本来就会报 drift |
| 10:04:59 | `cli night` `apply=0` | 同上 |

自动调度的 62 条心跳**全部 Ok**，一条都没错。改为只统计 `trigger=schedule`；手动 / 演练 /
安装触发的非 Ok 单独计数，写在证据列里，不参与判定。手动 `apply=0` 的语义就是「只核对不写入」，
报 drift 是设计行为，不是缺陷。

**A24「CI 自动测试通过」**

原先把 `$PSScriptRoot` 的上一级当成仓库根。但部署树只带 `powershell/` 与二进制，**不含
`.github/` 与 `tests/`** —— 在运行目录里跑这个脚本必然判 `FAIL 缺少 .github/workflows/ci.yml`。
那是部署内容的一部分，不等于 CI 缺失。

改为探测仓库根，依次试：

1. `$PSScriptRoot` 的上一级（源码树里就是仓库根）
2. 上两级的 `_publish\quiet-windows-toolkit`
3. 上一级的 `_publish\quiet-windows-toolkit`

取第一个含 `.github/workflows/ci.yml` 的。都找不到时判 `MANUAL` 并提示到源码树重跑，
不再误报 `FAIL`。

### 实测

源码树：`PASS 22 / FAIL 0 / 待人工 1`（A04 SysMain 必须夜跑期判）。A11 / A12 / A13 / A14 / A17
五项由 11-15 10:02 日链转 PASS。`ci.yml 在位；本机 Pester 79/79 全绿`。

## 2.0.1 — 2026-11-15

### 修复：夜间窗口里「立即联机硬盘」把盘又脱了一次

真机报障：夜间静音生效时，托盘点「立即联机硬盘（恢复白天状态）」无效，但「关闭静音模式」可用。

根因在 `ReconcilePlanner.Build` 的相位判定：

```csharp
bool night = targetNight.HasValue ? targetNight.Value : p.Action == "night";
```

`targetNight` 描述的是「此刻时间窗口应该是什么相位」，由 `Decision.TargetNight` 传入。夜间窗口里
恒为 `true`，于是**显式要求 day 也会被判成 night**，展开出来的是整条夜间链：

```
1. maintenance night     ← 又把维护任务停一遍
2. disk        offline   ← 盘刚刚联机，立刻被再脱一次
3. fan         silent
```

托盘的「立即联机硬盘」走的正是 `qmctl reconcile day --apply`，所以它不但没把盘放回来，
还主动又脱了一次。「关闭静音模式」走 `qmctl apply manual-normal`，决策变成 `ManualNormal`
（`TargetNight=false`），恰好绕过了这个缺陷 —— 这正是两者一个坏一个好用的原因。

对称的缺陷同样存在：白天窗口里显式 `reconcile night` 会被展开成白天链。

修法是把优先级摆正 —— **显式动作 > 目标相位**，`targetNight` 只留给「只读核对」（`none` / `verify`）：

```csharp
bool night;
if (p.Action == "night") night = true;
else if (p.Action == "day" || p.Action == "restore") night = false;
else night = targetNight.HasValue && targetNight.Value;
```

`RepairTarget` 返回的相位始终与 `targetNight` 一致，不受影响。

### 变更：托盘「立即联机硬盘」在静音中改为关静音

即使相位修对，夜间窗口里只跑一次 `reconcile day` 也只生效到下一次 10 分钟心跳 ——
`ApplyRun("day")` 会把 `AutoSilentActive` 置 false，下一次心跳按窗口判定为夜间，
盘又被脱回去。用户看到的仍是「刚点完又掉了」。

这个菜单项的语义本来就是「我现在要用盘」，所以静音开着时改为走 `apply manual-normal`
（写抑制标记，本夜不再自动进夜，并执行白天链）；静音没开时才走 `reconcile day`。

### 新增：相位与窗口相反时的显式提示

`qmctl reconcile day`（夜间）或 `reconcile night`（白天）现在会多一行说明：
本次只生效到下一次心跳，要整段保持请用 `qmctl apply manual-normal` / `manual-quiet`。
不提示的话，用户会以为「点完又掉了」是操作失败。

### 测试

C# 单测 118 → **122**，新增四例钉死这条边界：显式 day 在夜间窗口必须 online、
显式 night 在白天窗口必须 offline、restore 与 day 同链且不被改写、
`targetNight` 为 null 时仍按动作走。

## 2.0.0 — 2026-11-15

v2 重构收尾。Phase 0–7 全部完成，`ProductInfo.Version` 从 `2.0.0-rc.1` 抬到 `2.0.0`。

### 新增：安全边界文档

- `SAFETY.md`（245 行）：写面清单 16 项逐条列到「哪个任务 / 哪个注册表值 / 哪个磁盘」；禁改范围 6 条前缀；磁盘硬阻断 6 种原因；失败语义 4 条（默认演练、写后必回读、未提权不做一半、Worker 不做判断）；三条不变量（单调度器 / 看不见不等于不存在 / 没回读不算成功）；五级退路（手动静音 → Repair → Reset-Baseline → Uninstall → Rollback）；已知风险 7 条诚实清单。
- README 顶部加了指向；`目录` 段补齐 `src/` `tests/` `.github/` 真实结构。

### 新增：PowerShell 模块的 Pester 测试（79 条断言）

- `tests/ps/` 六个文件，覆盖 `Common` / `Config` / `Disk` / `Maintenance` / `Power` / `Security`。
- 选点原则是钉死历史故障，不是追覆盖率：`ConvertFrom-QmTaskHResult` 的 `267011`（从未运行）不得判成 NotPresent；`Resolve-QmSafeChild` 必须拒绝 `..`、盘符前缀、`:`（NTFS ADS）；`Get-QmDiskIdleSeconds` 的「倒数第二 = AC、最后 = DC」顺序锁死；`Format-QmSize` 的 1023/1024 进位边界；维护任务启用位读不到时不得当「已停用」。
- `Write-QuietModeLog` 断言追加而非覆盖、父目录自建、行首 `[yyyy-MM-dd HH:mm:ss] LEVEL` 格式。

### 新增：Windows CI

- `.github/workflows/ci.yml`，`windows-latest`：全解决方案 `dotnet build` → C# 单测 → `qmctl --help` 产物校验 → 装 Pester 5.7 → `tests/ps` → 上传 NUnit XML。
- 不需要 Visual Studio：SDK 风格 csproj + `Microsoft.NETFramework.ReferenceAssemblies` 足以在 `dotnet build` 下构建 net481。

### 变更：A24 不再是人工项

- `Verify-NightRun.ps1` 的 A24 三段判定：缺 `ci.yml` → FAIL；有 `ci.yml` 但本机无 Pester 5.7+ → MANUAL；有 `ci.yml` 且本机同批用例全绿 → PASS。不查远端 API，验收脚本保持只读离线可用。

### 新增：Rollback 的演练路径

- `Install-QuietMode.ps1` 新增 `-DryRun`：Rollback 分支过去完全没有演练守卫（`$apply` 在分支之后才赋值），现在每个写操作都判。
- 权限门放行「Rollback + DryRun」的非提权演练。
- 备份还原从黑名单（跳过 `*.json` / `*.log`）改**白名单**（六个二进制文件名）—— 旧逻辑会把 `migration-before.txt` 复制进部署目录。

### 修复：删掉 v2 任务后谎报「重复调度」

- `SchedulerStatus.Conflict` 在 `Exists == false` 时直接返回 false。回滚后 v2 任务已删、v1 闸门启用，旧逻辑仍报「存在重复调度」并让 `task status` 退出码 2 —— 一次完全成功的回滚在脚本看来是失败。
- `Exists == null`（任务读不到）**保持保守报冲突**：看不见不等于不存在。
- `ToText()` 新增回滚态两个分支，并引入 `LegacyActive` 区分「v1 接管」与「当前没有调度器」。
- `Uninstall-QuietMode.ps1` 第 160 行读同一个标志，旧逻辑让每次正常卸载都多一条假 WARN。

### 修复：`state.json` 说真话

- `controllerState` 过去只在 `run.Decision != null`（非 `none`）时写回，一次 `action=none` 的心跳就会让它永远停在 `EnteringNight` —— `reconcile.log` 与 `qmctl status` 显示 `NightActive`，状态文件却说还在进夜。现在收尾落到稳定态，版本号抬到当前值。

### 修复：安装器的中文输出乱码

- `Install-QuietMode.ps1` 是 17 个脚本里唯一没设 `Console.OutputEncoding` 的那个。`qmctl` 自己按 UTF-8 输出，提权会话按 936 解码，日志里就出现 `宸叉敞鍐?`。同一台机器同一 exe 的对照实测确认后补上该行。

### 实测记录

- 首夜（11-14 23:58）五步链 18 秒跑完：`maintenance/night` 23:58:01 → `verify/night` 23:58:19，5/5 全 `done`，`verdict=Ok`。
- 之后 00:08、00:18 两次 10 分钟心跳均为 `action=none` —— 手动静音不再重跑整条夜链。
- 验收表 18 PASS / 0 FAIL / 5 待 10:02 日链（A11 A12 A13 A14 A17）。
- `Install -Mode Rollback` 与 `Uninstall -Mode Apply` 均已真机跑过并装回：回滚后二进制回到 58,880 / 133,120，`migration-before.txt` 未进部署目录；卸载后八条判据（v2 任务消失、5 盘 Online、7 项维护 enabled、SysMain `Start=2`/Running、`MaintenanceBoundary=10`、`WakeUp=0`、DISKIDLE 1200s、托盘退出）全部实测通过。

## 2.0.0-rc.3 — 2026-11-14

### 修复：手动静音每十分钟把整条夜间链重跑一遍

- `StateMachine.Decide` 的 `ModeOn` 分支过去**无条件**返回 `action=night`。手动静音一旦
  生效，每根 10 分钟心跳都会执行完整五步：磁盘反复置离线、8 个维护任务反复禁用、风扇
  配置反复重载。2026-11-14 首夜实测留下三条完整记录（00:49 / 00:58 / 01:08），全是同
  一件已经做完的事。
- 改成和自动夜间一致：`AutoSilentActive` 已为 true 且上次没失败 → `action=none`，只观测
  不写盘；漂移由回读后的 `RepairTarget` 强制补跑一次，真掉队照样会修。

### 修复：上次开机前的手动静音会在白天开机时把机器重新静音

- 手动静音是持续覆盖，会跨关机活下来。实测链路：00:49 手动静音 → 01:09 关机 →
  12:35 开机 → 12:36 与 12:37 各跑一遍夜间链 → 中午两块机械盘脱机、维护任务禁用、
  风扇静音，直到 12:42 手动切回白天。行为符合「手动优先」，但没有任何提示，人看到的
  只是「中午盘掉线了」。
- `state.json` 新增 `modeOnSinceUtc`（schema 4 → 5）。`StateMachine.ExpireStaleManualOverride`
  在白天窗口比较「按下时刻」与「本次开机时刻」：早于开机的一律清除并写进 `Decision.Notes`；
  本次开机之后按下的一律保留（留两分钟余量吸收时钟毛刺）。
- 旧状态没有时间戳时退回「开机不满 10 分钟」这个保守判据，别的时候不猜。

## 2.0.0-rc.2 — 2026-11-13

### 修复：加固定位到写权限模型，安装器不再被自己的加固锁死

- **旧 Deny 残留会让 Apply 死在第一步。** 上一版加固给 Users 加了 Deny（含 `Write`、
  `ChangePermissions`），管理员也在 Users 组里，于是 `recovery\` 下连
  `install-<时间戳>` 备份目录都建不出来 —— 实测 `New-Item` 抛
  `UnauthorizedAccessException`，整个 Apply 在第 8 步退出，一个字节都没部署。
  安装器现在把 `Unblock-QmLegacyDeny` 排在**第一个写操作之前**，失败还有
  `takeown /a` + `icacls /remove:d` 兜底。
- **「普通用户」按 SID 判全。** 原来只看 `S-1-5-32-545`，而部署目录在 `H:\` 下，
  从 `H:\AI工作区` 继承来的 `Authenticated Users:Modify` 是一整条写权限。结果加固
  报告「已是只读」，普通用户照样能改 SYSTEM 要执行的脚本。现在按
  Users + Authenticated Users + Everyone 三个 SID 判与削。
- **可写判定不再用超集掩码。** `FullControl` / `Modify` 拿去做 `-band` 会让
  `ReadAndExecute` 也算命中（`131241 & 2032127 != 0`），六条只读路径全被报成
  「普通用户可写」。改判原子位（Write / Delete / TakeOwnership / ChangePermissions）。
- `Set-QmAcl` 用 `FileSystemInfo.SetAccessControl` 而不是 `Set-Acl`：后者回写整份安全
  描述符（含 SACL），非提权会话直接报 `SeSecurityPrivilege`。
- `Test-Privilege.ps1` 10 → 21 项：旧 Deny 下建子目录确实被拒、`Remove-QmUserDeny`
  能拆掉含 `ChangePermissions` 的 Deny、加固后 Authenticated Users 也不再有写权、
  加固后 Administrators 仍保留完全控制（提权安装器还要写备份）。

### 修复：托盘非提权看不到任务状态（任务对象自带一份安全描述符）

- **改任务文件的 ACL 没用。** Task Scheduler 在注册那一刻把任务的 SD 固化进
  `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\<路径>\SD`
  （自相对二进制），访问检查用的是这一份；`C:\Windows\System32\Tasks\...` 的 ACL
  只影响读文件。给文件加 `Users:ReadAndExecute` 后回读全 OK，托盘照样 0x80070005。
- **只给文件夹 ACE 也不够。** `\QuietHDD\` 文件夹补了 `(A;OICI;FR;;;BU)`，里面
  **已注册**的两个 v1 闸门依旧不可读 —— 文件夹 ACE 只让以后注册的任务继承到。
- `SchedulerInstaller.Install` 现在注册时显式传 SDDL：
  `D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;BU)`（管理员/系统全控，普通用户读）。
  传 `null` 拿到的默认 SD 只有 BA/SY 两条。
- 新增 `powershell/Fix-TaskVisibility.ps1`：多目标任务修复器。先试
  `IRegisteredTask::SetSecurityDescriptor`（不删任务）；回读为空就走删+重建，
  用任务自己的 XML 原样重新注册（只换 `<SecurityDescriptor>`），按原 `Enabled`
  回填，核对 `State`/触发器数与改前一致。备份到 `recovery\task-visibility-<时间戳>\`。
- `Grant-TaskRead.ps1` 新增 `-IncludeTasks`：`GetTasks(1)` 枚举 → 逐个
  `GetSecurityDescriptor` / `SetSecurityDescriptor` 追加 `(A;;FR;;;BU)`，
  不删任务、不重注册、触发器与 principal 一个字节都不动；原值先备份到
  `%TEMP%\qm-tasksd-<时间戳>\`。
- **回读必须重新取对象。** 实测 22:19 那一轮两个 v1 闸门都报
  「服务没接受」——其实是假阴性：`SetSecurityDescriptor` 之后手里那个
  `IRegisteredTask` 就失效了，同一个对象立刻 `GetSecurityDescriptor` 返回空串。
  下一轮非提权复验时它们早就有 `(A;;FR;;;BU)` 了。现在统一走
  `Wait-SecurityDescriptor`（重新 `GetTask`，最多 8 次、每次 150ms）。
- 踩过的坑写进脚本注释：`ITaskFolder::GetFolder` 不接受结尾反斜杠（0x8007007B）；
  SDDL 的控制位（`AI`/`PAI`）必须紧跟 `D:`，ACE 只能插在它后面；
  `FR` 是 `FILE_GENERIC_READ`，不等于 `GR`（`0x80000000`），手写掩码会写错权限。

### 新增：`Verify-NightRun.ps1`（验收自动化，只读）

- 对着 `worker.log` 的分步记录、`reconcile.log` 心跳、`state.json` 与
  `qmctl --json status` 的实时观测，逐条判定 `V2_SPEC §12` 的 A01–A23；
  `-Md <路径>` 直接产出证据表（含夜链/日链分步明细）。
- 只读，不需要管理员；FAIL 时打印缺哪一步、哪条心跳不 Ok。
- 本机入口 `verify-night.cmd`（GBK + CRLF，双击即跑，日志 `_verify-night.log`）。

### 新增：`Reset-Baseline.ps1`（重建 baseline，带相位闸门）

- baseline 记的是维护任务的**原始状态**，schema 2 的基线会一直被
  `snapshot verify` 报「旧 schema（2，当前 3）」，这条 WARN 只能重建消掉。
- 重建前先看 `state.json` 的 `controllerState` / `lastReconcileAction`：
  **夜间相位直接拒绝**（退出码 3）。夜里采会把「已禁用」记成原始状态，
  之后每天白天都会把维护任务恢复成禁用。确需强行重建加 `-Force`。
- 旧 `baseline.json` 先复制到 `recovery\baseline-<时间戳>\` 并校验 SHA256，
  备份失败就放弃重建（不赌「大概没事」）。
- 重建后回读：`snapshot verify` 不能再有旧 schema 告警、`complete=False` 要变
  `True`、`maint status` 的 BASELINE 列要与 NOW 列一致。
- 本机入口 `reset-baseline.cmd`（自提权，GBK + CRLF，日志 `_baseline.log`）。

## 2.0.0-rc.1 — 2026-11-12

这一版只做可靠性、安装迁移与恢复，不加新功能。**基线记录见 [`docs/RC_BASELINE.md`](docs/RC_BASELINE.md)**：
里面逐条写了验证了什么、没验证什么，未验证项不得当成已通过。

### 修复：手动关闭静音只覆盖本夜

- 抑制标记原来是个布尔值，白天恢复成功时被清掉，于是「今晚不再自动开」在
  00:5x 之后失效、本夜又自动重开。现在抑制绑定到**夜间窗口身份**
  （窗口开始日期，`NightWindow.Key`），存进 `state.autoSilentSuppressedWindow`；
  跨午夜、重启、改时间之后都按同一个窗口判定。
- `StateMachine.ApplyRun` 的 day 分支不再清抑制；只有 `rearm` / `manual-quiet` /
  `auto` 或进入下一个窗口才清。schema 3→4 自动迁移，旧标记按当前窗口认领，
  不会永久粘住。

### 修复：漂移四分类，读不到不再当失败

- 新增 `DriftClassifier`：`Compliant` / `ActionableDrift` / `UnfixableDrift` /
  `ObservationUnknown` / `SafetyBlocked`。
- 只有 `ActionableDrift` 允许自动重跑；未知与安全阻塞既不判成功、也不盲写。
- 维护任务被外部重新启用、DISKIDLE 偏离且 `enabled=true`、风扇档位被切回 —— 算可修复；
  磁盘身份不明 —— 算安全阻塞（绝不自动脱机）。
- `state.json` 持久化 `lastDrift` / `lastProblems`；心跳行追加 `drift=` 与 `why=`，
  `qmctl status` 能直接看到「为什么这一轮没通过」。

### 修复：PowerShell 任务三态

- `Get-QmTaskState` / `Invoke-QmStopTask` / `Invoke-QmSetTaskEnabled` 现在严格区分
  `true` / `false` / `null`：按 COM `HRESULT` 低位码定性（2/3 = 任务不存在，
  5 = 拒绝访问 = 未知），不靠中英文错误文本猜。
- 权限不足不再当成「任务不存在」；写入后回读仍为 `null` 判验证失败。
- 新增 `powershell/Test-TaskState.ps1`（13 项只读断言，PS 5.1 与 7.x 均通过）。

### 修复：维护恢复的 Critical 语义

- `s.Critical = desired` —— 原本启用的任务白天恢复不回来才是关键失败；
  原本禁用的任务「收回禁用」失败也照样报告，但不升级成灾难。

### 修复：唯一调度器

- v1 遗留任务路径补齐为 4 条（含/不含 `\QuietHDD\` 文件夹的两种历史写法）。
- 任务 XML 生成迁入 `QuietMode.Core.Scheduling.TaskXml`（可离线单测）：夜间
  Repetition Duration 按窗口计算（23:58→10:02 = `PT10H4M`，原来写死 10 小时，
  窗口一改就漏），白天触发器加有界重试（`PT4H` / 每 `PT30M`）。
- 已确认 `WakeToRun=false`、忽略并发、10 分钟执行上限。

### 修复：baseline 完整性与旧版本兼容

- 新增 `BaselineHealth` 四态：`Missing` / `Corrupt` / `Partial` / `Complete`。
  危险写操作只允许 `Complete` 或 `Partial`；`Corrupt` 一个字节都不用。
- `snapshot verify` 输出 `state`，CLI 把损坏基线当「没有基线」处理。
- 旧 schema 必须能读：只拒绝**比本版本更新**的 schema；升级不会把用户已有基线
  判成损坏。
- `baseline.previous.json` 只在现基线可信时轮换，不再无条件覆盖。
- 白天没有可用基线时判 `Unknown`，不宣称「已完整恢复」。

### 新增：baseline 记录「原始启用状态未知」

- 夜间装机会在维护任务被闸门禁用的时候拍快照，此时观测到的 `enabled=false`
  并不是原始状态。现在这种条目打 `originalUnknown=true` 并写明原因
  （baseline schema 2→3）。
- 白天计划遇到未知项保留 Pending 并说明，不启用也不禁用；白天验证判「无法确认」，
  不判通过。`qmctl maint baseline-enable/disable` 视为用户确认，清除未知标记。
- 新增字段默认值不落盘，所以升级不会让已有基线的完整性校验值失配。

### 新增：安装器七段预检与单调度不变量

`Install-QuietMode.ps1` 默认 DryRun，预检分七段，任一 FAIL 都不动系统：

1. 环境：PowerShell ≥ 5.1、.NET 4.8、Windows 版本、管理员
2. 载体：qmctl / Core / Windows / 托盘 + 7 个模块 + Worker + 操作脚本（大小、版本、sha256）
3. 现有安装：config / baseline、v1 三份配置、v1 四条历史任务、v2 任务
4. 磁盘稳定身份：每个目标盘必须 `matched` 且有 `uniqueId`，禁止按编号猜测
5. 静音窗口：`HH:mm` 合法性、窗口长度、当前判定；FanControl 依赖与档位
6. baseline 可信度：四态判定，`Corrupt` 直接拒绝
7. 安装计划：把要做的 8 步和不变量先打印出来

配套：改动前备份配置/任务 XML 并写 `migration-before.txt`；v2 注册后回读不到就退出、
不动 v1；v1 停不掉就回滚 v2；收尾再复核一次没有并行调度。

### 新增：卸载 / 恢复与体检脚本

- `Uninstall-QuietMode.ps1`：停调度 → 确认已停 → 磁盘联机 → 维护任务 → SysMain/注册表
  → 电源 → 风扇 → 托盘。顺序按「数据可访问性优先」，每步回读，失败保留
  baseline / recovery / logs，可重复执行。
- `Repair-QuietMode.ps1`：只诊断（文件 / 配置 / baseline / 调度与心跳 / 决策 / 组件 /
  权限），`-Apply` 才做三件低风险修复。
- 两者默认都不改系统；DryRun 均实跑通过。

### 安全：特权目录不再跟随预置路径

- 新增 `PrivilegedIo`：请求/结果文件名带随机 nonce；写请求用 `FileMode.CreateNew`
  （目标已存在即失败，绝不覆盖、绝不跟随链接）；目录或结果文件是重解析点直接拒绝。
- 新增 `powershell/Modules/QuietMode.Security.psm1`：ACL 体检与加固（只加 Deny ACE，
  不改其它系统策略；`logs` 单独放行追加）。
- 新增 `powershell/Test-Privilege.ps1`：实机 ACL 只读体检 + 临时目录里的攻击自测
  （junction、`..\` 逃逸、绝对路径、盘符、演练模式不改权限），10 项全通过，已并入
  `Test-QuietMode.ps1`。

### 其它

- `ProductInfo.Version` 从 `2.0.0-phase5` 更新为 `2.0.0-rc.1`。
- 单元测试 89 → 106 项。

## 2.0.0-phase6.2 — 2026-11-12

### 新增：reconcile 心跳（`logs/reconcile.log`）

- 每次 reconcile 都追加一行，**包括决策为 `none` / `verify` 的那种**：
  `时间 trigger=… action=… apply=0/1 verdict=… state=…`
- 修的是一个观测盲区：无事可做时既不写请求文件、也不写 `worker.log`，
  于是「任务没触发」和「触发了但没活干」在磁盘上完全同形。首夜之后 00:28 / 00:38
  两次「消失」就是这么来的 —— 查无可查。
- 超过 2 MB 自动滚到 `reconcile.log.1`。
- `qmctl task status` 增加「调度心跳」一行与 JSON 字段 `heartbeat` / `heartbeatMinutesAgo`。

### 修复：`task status` 误报重复调度

- `\QuietHDD\` 两个闸门在 ACL 挡住时 `Exists=true / Enabled=null`，
  旧逻辑把 `Enabled` 为 null 当成「仍在启用」，于是每次都输出「存在重复调度」。
  现在算「无法确认」，按「可能有重复调度」处理但不再断言。

## 2.0.0-phase6.1 — 2026-11-12

`DISKIDLE` 进入 reconcile 链。**这是一次会改系统状态的变更**：首轮 reconcile 会把
`DISKIDLE` 从实测的 300 秒写成配置里的 1200 秒。

### 修正

- **配置写 20 分钟、系统里却是 300 秒**。`spinDown` 从 Phase 5 起只是「独立开关」，
  `reconcile` 链根本不碰它，于是 `config.json` 与电源方案长期分叉，谁也没发现。
  现在 `spinDown.enabled=true` 时 `spindown` 是夜间/白天链上固定一步：
  `ReconcilePlanner` 排步 → worker 调 `Invoke-QmSpinDown` 写 AC/DC + `setactive`
  → C# 用 `PowerPlanReader` 独立回读 → 不一致判 `Failed`。`enabled=false` 时整步
  `skipped`，一个字节都不写。

### 新增

- `powershell/Modules/QuietMode.Power.psm1`：`Get-QmDiskIdleSeconds`（`powercfg /query`
  解析十六进制索引，失败读注册表）、`Invoke-QmSpinDown`（已是目标值则跳过 `setactive`，
  写完回读，只认回读值）。
- `ReconcileModels.SpinDownObservation`：`expectedSeconds` / `acSeconds` / `dcSeconds` /
  `captured` / `matches`，DC 只在读得到时参与判定。
- `qmctl spin show` 增加 `expectedSeconds` / `reconcileManaged` / `matches`，
  文本模式直接点出「配置已启用，next reconcile 会拉回 Ns」。
- `qmctl spin set` 在配置已启用且数值不同时提示会被 reconcile 覆盖，并给出
  `config set spinDown.minutes` 的正确用法。
- 单测 83 → 86（夜间/白天五步顺序、停转开启/关闭、`DISKIDLE` 不符判 Failed、读不到判 Degraded）。

## 2.0.0-phase5 — 2026-11-11

托盘重构：从“什么都干”的单文件变成 `qmctl` 的前端。**不改变运行中的系统状态**：
本阶段只动代码与只读探测，未执行任何 `--apply`。

### 新增

- `QuietMode/` 分层（原 2424 行 `QuietMode.cs` 已删除）：
  - `Program.cs`：入口、单实例、`--diag` / `--status` / `--selftest`。
  - `App/AppPaths.cs`、`App/AutoStart.cs`、`App/TrayLog.cs`：路径与 qmctl 定位、HKCU 自启与托盘常显、日志。
  - `Core/QmctlClient.cs`：唯一的 qmctl 调用通道（含 `runas` 提权与 UAC 取消识别）。
  - `Core/MiniJson.cs`：只读 JSON 解析，托盘不引用 `QuietMode.Core.dll`。
  - `Ui/TrayApp.cs`、`Ui/SettingsForm.cs`、`Ui/Icons.cs`：菜单、设置、图标。
  - `Diag/Diagnostics.cs`：自检输出。
- `qmctl status [--json]`：一次性观测（窗口、决策、控制器状态、风扇、磁盘、停转、计划任务、baseline）。
- `qmctl apply <manual-quiet|manual-normal|auto|pause|resume|rearm> [--apply]`：
  一次用户动作 = 一次提权 = 一次 reconcile，意图与结果分离。
- `qmctl config set <key> <value>`：白名单键 + 类型先挡 + `ConfigStore` 强校验；
  `--force` 跳过文件存在性检查。
- `qmctl spin show` / `qmctl spin set <分钟> [--apply]` + `SpinDownController`：
  DISKIDLE 独立开关，`powercfg` 写入后独立回读（`powercfg /query` 优先，注册表兜底）。
- `docs/TRAY.md`：分层、JSON 契约、图标语义、失败语义、已知取舍。
- 单测新增 5 项（`SystemClock`、`IsLastRunFailed`、状态快照契约）；
  `Test-QuietMode.ps1` 新增“编译托盘”“托盘契约：status 字段齐全（只读）”两步。

### 修正

- **开机超过 24.9 天后夜间窗口会被误判成白天**。`Environment.TickCount & 0x7FFFFFFF`
  回绕后算出的“运行时长”变成几毫秒，落进 `schedule.startupGraceMinutes`（15 分钟），
  于是 06:00–10:02 被判为“刚开机、按白天处理”。改用 `GetTickCount64`（`SystemClock.Uptime()`），
  `ReconcileController` 与 `StatusProbe` 共用。
- **`IsLastRunFailed` 有两份实现**（ReconcileController 私有 + 托盘将要有自己的）。
  收进 `StateMachine.IsLastRunFailed`，`NotRun` 明确不算失败。
- **托盘状态显示不再“看不见就当正常”**：`offline` / `exists` 读不到时 JSON 给 `null`，
  界面显示“状态未知”；计划任务未注册、v1 闸门仍启用/读不到都变成常驻警告。

### 移除

- 托盘里的 `Apply()`、`CheckAutoSilent()`、`BootReconcile()`、`Sys.BuildWorker/BuildGateScript/BuildSpinScript`、
  `--fixgate`、`--fixhwinfo`：这些行为全部由 `qmctl reconcile` 与计划任务接管。
- 托盘对 `settings.ini` / `state.ini` 的读写：配置与状态只剩 `config.json` / `state.json` 一个写入口。
- 托盘的 `System.Management` 依赖（WMI 探测移到 `qmctl status`）。

### 升级注意

- 托盘现在需要同目录的 `qmctl.exe`（或 `tray.ini` 的 `QmctlPath`）。找不到时菜单全部禁用并提示，
  **不会**退回旧逻辑。
- 每次切换静音模式会弹一次 UAC：v1 在“夜间自动静音中手动关闭”时不弹，v2 为了“意图+执行+验证”
  原子化接受这个代价（见 `docs/TRAY.md` §7）。
- 自动切换仍依赖 `\QuietMode\QuietMode-Reconcile`；本阶段**未注册**该任务（Phase 6 做迁移安装），
  所以当前运行环境下自动切换不会发生 —— 托盘会在菜单第三行常驻提示。

### 测试

- 单测 81 项全过。
- `Test-QuietMode.ps1` 全绿：编译 3 项、单测、配置校验、托盘契约、磁盘安全 5 项、维护安全 6 项、调度安全 10 项。
- 实机只读验证：`qmctl status` 报 `DayActive` / `action=none`，风扇 `disabled`（临时 root 默认策略）、
  DISKIDLE `AC=300s DC=0s`；`task status` 正确报“v2 任务不存在 + v1 闸门可能仍在”。
- 托盘冒烟：`--selftest` 三条路径（正常配置 / 配置缺失 / qmctl 缺失）均无异常退出，日志无堆栈。

## 2.0.0-phase4 — 2026-11-11

统一 Worker 与调度。**不改变运行中的系统状态**：默认演练，`--apply` 才动手，未提权必然失败。

### 新增

- `src/QuietMode.Core/Scheduling/`
  - `ScheduleWindow.cs`：完整 `HH:mm` 的跨午夜窗口判定 + 显式开机补偿，带原因输出。
  - `ControllerState.cs`：10 个控制器状态 + `StateMachine`（唯一决策点）+ `ApplyRun`（唯一状态写回点）。
  - `ReconcileModels.cs`：reconcile 计划 / 步骤结果 / 磁盘与风扇观测 / 三档验证结论。
  - `ReconcilePlanner.cs`：夜间与白天两条固定顺序 + `ReconcileVerifier`（全组件验证，缺一项不报成功）。
- `src/QuietMode.Windows/ReconcileController.cs`：决策 → 计划 → 调 Worker → 复观测 → 独立验证 → 写回状态。
- `src/QuietMode.Windows/SchedulerInstaller.cs`：`\QuietMode\QuietMode-Reconcile` 的 XML 生成、注册、卸载、v1 闸门检查与停用。
- `src/QuietMode.Windows/Native/FanProbe.cs`：`IpcCtl.exe list` 解析 `current=`，风扇配置真实回读。
- `powershell/Worker/Invoke-QuietModeWorker.ps1`：唯一执行器（`-Reconcile/-EnterNight/-EnterDay/...` 统一到请求 JSON）。
- `powershell/Modules/QuietMode.Fan.psm1`：风扇切换唯一写入口，切换后回读比对。
- `powershell/Test-SchedulingSafety.ps1`：只读集成测试 9 项。
- `qmctl reconcile`、`qmctl task status|print|install|uninstall|disable-legacy`、`qmctl state pause|resume|rearm|manual-quiet|manual-normal|auto`。
- `docs/SCHEDULING.md`。

### 修正

- **风扇状态不再靠猜**。v1 注释写「FanControl 未暴露当前配置名」，实测 `IpcCtl.exe list` 直接返回 `current=<文件名> folder=<目录>`，v2 用它做回读验证。
- **v1 闸门读取失败不再等于「没有冲突」**。`\QuietHDD\` 的 ACL 让普通用户读不到任务，`task status` 现在把「读不到」判为可能冲突，而不是报「没有重复调度」。
- `QuietModeState` schema 3：新增 `lastVerdict` / `lastErrorCode` / `lastReconcileAction` / `consecutiveFailures` / `controllerState`。

### 测试

- 单测 76 项（新增 16 项：时间窗口 3、状态机 6、计划顺序 3、验证 4）。
- `Test-SchedulingSafety.ps1` 9 项全过，并挂进 `Test-QuietMode.ps1`。
- 实机：`reconcile` 演练在 22:47 正确判为白天并给出 `DayActive`；`reconcile night` 演练输出 4 步计划与逐项失败原因（7 个任务仍启用、SysMain `Start=2`、两块盘 Online、风扇当前 `FanControl.json`），全程未改动任何状态。

## 2.0.0-phase3 — 2026-11-11

Windows 维护管理重构。**不改变运行中的系统状态**：默认演练，`--apply` 才动手，未提权必然失败。

### 新增

- `src/QuietMode.Core/Maintenance/`
  - `MaintenanceObservation.cs`：一次系统观测（任务 / 服务 / 注册表），`Present` / `NotPresent` / `Unknown` 三态。
  - `MaintenancePlan.cs`：步骤模型（8 种动作）+ 5 种执行状态，新增 `NotRun` 区分「没跑」与「跳过」。
  - `MaintenancePlanner.cs`：纯计划器，把规格 §4.2 / §4.3 的顺序固化成步骤列表。
  - `MaintenanceVerifier.cs`：纯验证器，输出 `Ok` / `Degraded` / `Failed`。
- `src/QuietMode.Windows/MaintenanceProbe.cs`：只读观测（Task Scheduler COM / 服务注册表 / 维护注册表）。
- `src/QuietMode.Windows/MaintenanceController.cs`：出计划 → 脚本执行 → **重新观测并独立验证**。
- `powershell/Modules/QuietMode.Maintenance.psm1`：每个写动作 `-ErrorAction Stop` + 写后复读。
- `powershell/Maintenance/Invoke-MaintenanceOperation.ps1`：维护写操作唯一入口。
- `powershell/Test-MaintenanceSafety.ps1`：只读集成测试（清单三态、计划只读、未提权必须失败）。
- `qmctl maint list|status|plan|night|day|verify|baseline-enable|baseline-disable`。
- `docs/MAINTENANCE.md`。
- `MaintenanceCatalog.SelfCheck()`：清单自检，禁止条目落进 Windows Update / Defender / Firewall 等禁改范围。

### 修正（相对 v1）

- v1 的六处 `$MaintTasks` 数组漏掉 `Data Integrity Scan for Crash Recovery`，还重复了一条 `Data Integrity Scan`；v2 只有一处清单，且有自检。
- `Regular Maintenance` 在本机**不存在**，v1 的"夜间禁用"一直是空操作；v2 按 `NotPresent` 处理并在输出里明说。
- v1 只禁不恢复（`Regular Maintenance` 尤其）；v2 白天按 baseline 恢复，baseline 没记录的值一律不写。
- v1 直接杀进程解决问题；v2 只按任务路径 `Stop-ScheduledTask`，不碰同名进程。
- v1 靠"命令没报错"判断成功；v2 每次写后复读，再由 C# 独立复观测定结论。

### 验证

- 单测 60 项全过（新增 17 项：清单自检 2、夜间计划/验证 5、白天计划/验证 7、状态语义 3）。
- `Test-QuietMode.ps1` → `ALL PASS`（新增「维护管理（只读集成测试）」「维护计划与验证」两步）。
- 实机：`maint plan night` 11 步、`maint plan day` 2 步、`maint verify night` → `Failed`（7 个任务仍启用 + SysMain 未禁用，符合当前真实状态）；`maint night --apply` 未提权 → 全部步骤标 `[未执行]`、退出码 2。
- 顺带修正 Phase 2 遗留的一处测试脆弱点：数据盘被占用时 `SAFETY_BLOCKED` 先于 `ELEVATION_REQUIRED` 生效，原断言只认后者。

## 2.0.0-phase2 — 2026-11-11

磁盘管理重构。**不改变任何运行中的磁盘状态**：默认演练，`--apply` 才动手，且未提权必然失败。

### 新增

- `src/QuietMode.Core/Disks/`
  - `DiskIdentityResolver.cs`：UniqueId 优先 / 序列号兜底 / 容量与总线复核；五种匹配状态；磁盘编号不参与匹配。
  - `DiskSafetyGuard.cs`：系统盘、启动盘、页面文件、存储池、状态未知一律硬阻断；force 仅越过「占用」。
  - `DiskOperationResult.cs`：规格 §3.4 的结构化结果，`Complete()` 强制「回读一致才算成功」。
  - `DiskOccupancy.cs`：占用诊断模型。
- `src/QuietMode.Core/Processes/ProcessRunner.cs`：并行读 stdout/stderr、真超时、超时杀进程树、区分启动失败/超时/非零退出。
- `src/QuietMode.Windows/DiskController.cs`：写请求 JSON → 调 PowerShell → 读结果 JSON。
- `src/QuietMode.Windows/Native/DiskProbe.cs`：卷 / 页面文件 / 转储文件 / 存储池 / I-O 计数器 / 进程线索只读探测。
- `powershell/Modules/QuietMode.Disk.psm1`：身份解析、安全闸门、`Set-Disk` + 回读、占用诊断。
- `powershell/Disks/Invoke-DiskOperation.ps1`：磁盘写操作的唯一入口（落盘前二次复检）。
- `powershell/Test-DiskSafety.ps1`：只读集成测试（系统盘必须被拦住、未提权不得报成功）。
- `qmctl disks resolve|inspect|offline|online|verify`。
- `docs/DISK_MANAGEMENT.md`。

### 修正（相对 v1）

- v1 的 `diskgate.ps1` 直接按磁盘编号 `Disks=0,1` 操作；v2 只按稳定身份匹配，且身份不唯一时拒绝执行。
- v1 只把「进程路径正则」当作占用检测；v2 以物理磁盘 I/O 计数器为主、进程线索为辅，并明确区分「无法判定」与「空闲」。
- v1 的 PowerShell 调用用 `ReadToEnd()` + `WaitForExit(timeout)`，存在死锁与假超时；v2 的 `ProcessRunner` 并行读取并真正终止超时进程树。
- `MSFT_Disk.HealthStatus` 是数值枚举，v1 直接转字符串得到 `0`；v2 映射为 `Healthy` / `Warning` / `Unhealthy`。
- 性能计数器实例名对无挂载卷的磁盘只有编号（`0` 而非 `0 C:`），按编号前缀匹配会漏判 —— 已修。

### 验证

- 单测 43 项全过（新增 22 项：身份解析 10、安全闸门 10、结果不变式 2）。
- `Test-QuietMode.ps1` → `ALL PASS`（含新增的「磁盘身份解析」「磁盘安全闸门」两步）。
- 实机：5 块盘全部唯一匹配；系统盘 `inspect --force` 仍判「禁止脱机」，`offline --apply --force` 返回 `SAFETY_BLOCKED`；数据盘演练不被误拦；未提权 `--apply` 返回 `ELEVATION_REQUIRED`。

## 2.0.0-phase1 — 2026-11-11

统一配置与快照系统。**不修改运行中的系统状态**，v1 脚本与托盘程序保持原样可用。

### 新增

- `src/QuietMode.Core/`
  - `Json/Json.cs`：零依赖、保序 JSON 读写（严格 RFC 8259 子集，错误带行列号）。
  - `Models/QuietModeConfig.cs`：v2 配置模型 + 默认策略。
  - `Models/QuietModeState.cs`：运行状态 + 恢复事务进度。
  - `Models/BaselineSnapshot.cs`：原始状态快照模型。
  - `Models/MaintenanceTask.cs`：**唯一**维护任务清单（`MaintenanceCatalog`）。
  - `Models/SnapshotIntegrity.cs`：快照完整性校验值。
  - `Models/OperationResult.cs`：统一操作结果（区分「已验证」与「仅请求」）。
  - `Configuration/ConfigStore.cs` / `ConfigValidator.cs` / `ConfigMigrator.cs` / `AtomicFile.cs` / `QuietModePaths.cs`。
- `src/QuietMode.Windows/`
  - `SnapshotManager.cs`：baseline 采集、保存、校验。
  - `Native/`：磁盘（MSFT_Disk）、计划任务（Task Scheduler COM）、服务、注册表、电源方案、Windows 版本、FanControl 只读探测。
- `src/QuietMode.Cli/`：`qmctl`，支持 `config` / `snapshot` / `state` / `disks` / `paths`。
- `powershell/Modules/QuietMode.Common.psm1`、`QuietMode.Config.psm1`，`Get-QuietModeStatus.ps1`、`Test-QuietMode.ps1`。
- `config/default-policy.json`、`QuietMode.sln`、`build/build.ps1`。
- `tests/QuietMode.Core.Tests/`：21 项单测（JSON、校验、原子写、last-known-good、迁移、state、快照完整性、任务清单）。
- `docs/CONFIGURATION.md`。

### 修正（相对 v1）

- DISKIDLE 设置 GUID 按实机核对改为 `6738e2c4-e8a5-4a42-b16a-e040e769756e`；此前的猜测值读不到真实电源设置，只能靠 `powercfg` 兜底。
- 计划任务读取改走 COM，避免 `schtasks` 控制台编码把中文名与 XML 弄坏；非提权读不到受保护任务时明确标 `exists=null` + "拒绝访问"，不再当成"不存在"。
- 无法解析稳定身份的磁盘不再写入配置（v1 用磁盘编号，无回退保护）。

### 兼容

- v1 文件（`settings.ini` / `state.ini` / `diskgate.ps1` / 托盘 exe）行为不变；`qmctl config migrate` 是只读迁移，写目标由 `--root` 决定。
