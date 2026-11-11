# 磁盘管理（v2 Phase 2）

> 对应规格 §3.1–§3.4。本文件说明 v2 如何确认「是哪块盘」、什么情况下**绝对不动手**、动手后如何证明真的生效。

## 1. 三条不变量

1. **编号不是身份。** 磁盘号会在插拔、BIOS 改动、盘序变化后漂移，只在当次运行内用于定位，绝不写入配置、绝不用于跨次匹配。
2. **不确定就不动手。** 身份不唯一、字段冲突、状态读不到 —— 全部拒绝，并说明原因；不换一个盘号重试、不操作「同型号的另一块盘」。
3. **成功必须回读。** 下发 `Set-Disk` 之后重新查询 `IsOffline`，与请求一致才算成功；只发命令不看结果的路径在 v2 里不存在。

## 2. 身份模型

配置里每块目标盘保存以下字段（`config.json` → `disks.targets[]`）：

| 字段 | 作用 | 缺失时 |
| --- | --- | --- |
| `uniqueId` | 首选身份（`MSFT_Disk.UniqueId`） | 退到序列号 |
| `serialNumber` | 辅助身份 / 复核 | 只告警 |
| `friendlyName` | 型号复核 | 只告警 |
| `sizeBytes` | 容量复核（容差 1 MiB） | 跳过该检查 |
| `busType` | 总线复核（SATA / NVMe / USB…） | 跳过该检查 |
| `legacyNumber` | v1 迁移留下的人眼线索 | 仅日志，**不参与匹配** |

### 匹配流程

```text
目标 → 按 uniqueId 精确匹配（归一化：trim + 大写）
        ├─ 命中 1 块  → 复核 serial / size / busType
        ├─ 命中多块    → ambiguous，拒绝
        └─ 未命中
             → 按 serialNumber 匹配（去空白 + 大写）
                 ├─ 命中 1 块 → 复核 size / busType（告警：较弱身份）
                 ├─ 命中多块  → ambiguous，拒绝
                 └─ 未命中    → not-found，拒绝
```

### 五种结果

| 状态 | 含义 | 是否允许操作 |
| --- | --- | --- |
| `matched` | 唯一命中且字段一致 | 是 |
| `not-found` | 当前磁盘里没有这块盘 | 否 |
| `ambiguous` | 多块盘同时命中 | 否 |
| `no-identity` | 配置里没有可用身份字段 | 否 |
| `conflict` | 命中了，但序列号 / 容量 / 总线不一致 | 否 |

`conflict` 的判定是刻意严格的：如果 `uniqueId` 对得上而序列号对不上，v2 视为「另一块盘」而不是「同一块盘刷了固件」。宁可不动，也不要在不确定时脱机。

## 3. 安全闸门

`DiskSafetyGuard` 在**两处**独立执行：C# 侧预检（展示 / 演练）与 PowerShell 侧落盘前复检（权威）。任一处不通过都不会下发命令。

### 硬阻断（force 也越不过）

- `IsBoot = true` → 启动盘
- `IsSystem = true` → 系统盘
- `IsBoot` / `IsSystem` 读不出来（`null`）→ 状态未知，按危险处理
- 卷标记为系统卷 / 启动卷
- 页面文件位于该盘（含自动管理页面文件的提示）
- 存储池成员
- 总线类型为 `Spaces` / `FileBackedVirtual`
- 磁盘编号无效
- 身份不唯一（见上一节）

### 只告警

- 崩溃转储文件位于该盘（脱机期间蓝屏写不了 dump）
- 健康状态非 `Healthy`、磁盘只读、USB 连接
- 该盘没有可识别的卷（裸盘 / 分区表异常）
- 型号串不一致（身份字段一致时仅提示）

### 只有这一项能被 force 越过

- **普通占用**：I/O 活跃或存在疑似占用进程。

`--force` 的语义是「我知道这些程序会被打断」，不是「我知道我在动系统盘」。这条边界由单测 `安全闸门：系统盘 / 启动盘禁止脱机` 与 `安全闸门：占用时 force 可越过` 双向锁定。

## 4. 占用诊断

v1 只用进程路径正则判断占用，这是不完整的。v2 的组合：

1. **物理磁盘 I/O 计数器**（`Win32_PerfFormattedData_PerfDisk_PhysicalDisk`，间隔约 0.7s 采样两次）：`DiskBytesPersec`、`PercentDiskTime`、`CurrentDiskQueueLength`。
   判定阈值：> 256 KB/s 或 > 15% 磁盘时间视为活跃。
2. **进程线索**（`Win32_Process` 的可执行路径 / 命令行是否引用该盘的盘符）：辅助判断，列 PID、进程名、命中原因。
3. **volumes**：该盘的卷、盘符、文件系统、可用空间。
4. **handle.exe**（可选）：存在才用，不存在不影响任何功能。

注意实例名格式：没有挂载卷的磁盘实例名只有编号（`0`），有挂载卷是 `1 D:` / `2 C: G:`。早期版本按 `"{n} "` 前缀匹配，会漏掉脱机磁盘 —— 已修。

计数器不可用时，报告明确写「无法判定（性能计数器不可用）」，不会假装「空闲」。

## 5. 执行流程

```text
qmctl disks offline 0 --apply
  └─ 读 config.json（校验通过才继续）
     └─ 选目标（索引 / all / uniqueId / serialNumber）
        └─ 写请求 JSON 到 <root>\run\disk-request-*.json   ← 不拼脚本正文
           └─ powershell -File powershell\Disks\Invoke-DiskOperation.ps1
              ├─ 重新枚举 Get-Disk，按身份复检（防 TOCTOU）
              ├─ Test-QmDiskSafety（含占用）
              ├─ 已是目标状态 → ALREADY_IN_STATE（成功，不重复下发）
              ├─ 再取一次同一 UniqueId 的磁盘对象（编号变了就中止）
              ├─ Set-Disk -InputObject $disk -IsOffline $true/$false
              └─ 最多 8 次 × 250ms 回读 IsOffline
           └─ 读结果 JSON → DiskOperationResult
```

请求与结果都走文件（UTF-8 无 BOM），既避开控制台代码页 936，也避免「从 stdout 里猜状态」。

## 6. 结果结构

`DiskOperationResult` 字段：`operation`、`success`、`verified`、`errorCode`、`errorMessage`、`uniqueId`、`serialNumber`、`friendlyName`、`currentDiskNumber`、`previousState`、`requestedState`、`actualState`、`timestamp`、`steps[]`、`warnings[]`。

`success` 只在 `actualState == requestedState` **且** `errorCode == OK` 时为 `true`（`Complete()` 里强制，单测覆盖）。演练模式下 `success=true` 但 `verified=false`。

### 错误码

| 码 | 含义 |
| --- | --- |
| `OK` | 成功且已验证 |
| `NO_TARGET` | 没有可操作的目标 |
| `NO_IDENTITY` | 目标没有稳定身份 |
| `IDENTITY_NOT_FOUND` | 当前磁盘里找不到 |
| `IDENTITY_AMBIGUOUS` | 多块盘同时命中 |
| `IDENTITY_CONFLICT` | 辅助字段冲突 / 执行前身份变化 |
| `SAFETY_BLOCKED` | 被安全闸门拒绝 |
| `BUSY` | 磁盘被占用 |
| `ALREADY_IN_STATE` | 已是目标状态，无需操作 |
| `ELEVATION_REQUIRED` | 需要管理员权限 |
| `SET_FAILED` | `Set-Disk` 报错 |
| `VERIFY_FAILED` | 回读状态与请求不符 |
| `TIMEOUT` / `PROBE_FAILED` / `SCRIPT_ERROR` / `NOT_SUPPORTED` | 超时 / 探测失败 / 脚本异常 / 不支持 |

## 7. 命令行

```powershell
# 本机磁盘与稳定身份（只读）
qmctl disks list

# 配置里的目标能否唯一对上当前磁盘
qmctl disks resolve

# 单块盘的完整画像：身份 / 卷 / 页面文件 / 存储池 / 占用 / 安全结论（只读）
qmctl disks inspect 0 --processes

# 演练（默认）：只算不做
qmctl disks offline 0
qmctl disks online  0

# 真正执行（需要管理员）
qmctl disks offline 0 --apply
qmctl disks online  all --apply

# 只回读当前状态
qmctl disks verify 0
```

退出码：`0` 成功 / `1` 一般失败 / `2` 校验失败或操作未达成。

## 8. Phase 2 验收对照

| 规格项 | 实现 | 证据 |
| --- | --- | --- |
| 唯一身份匹配（§3.1） | `DiskIdentityResolver` + PS `Resolve-QmDiskIdentity` | 单测 10 项 + 实机 5 盘全部 matched |
| 移除磁盘编号回退（§3.1） | 编号不参与匹配；`legacyNumber` 仅线索 | 单测「绝不回退到 Disk 0」 |
| 系统盘 / 启动盘保护（§3.2） | `DiskSafetyGuard` 双处执行 | 单测 + `Test-DiskSafety.ps1`（系统盘 `--apply --force` 仍 `SAFETY_BLOCKED`） |
| 页面文件保护（§3.2） | `Win32_PageFileSetting` + `PagingFiles` + 自动管理提示 | 单测两例（本盘阻断 / 他盘放行）；实机 G: 页面文件不误伤 D:/E: |
| 存储池保护（§3.2） | `Get-StoragePool` 成员按序列号比对；探测失败只告警 | 单测 + 实机（无池，正常放行） |
| 占用诊断（§3.3） | I/O 计数器 + 进程线索 + 可选 handle.exe | 实机读取到 0 B/s ~ 1.3 MB/s 区间 |
| Offline / Online + 验证（§3.4） | `Invoke-QmDiskState` 回读 8 次 | 未提权路径返回 `ELEVATION_REQUIRED`，不报成功 |
| Pause/Resume/Restore 配置来源 | `Invoke-DiskOperation.ps1` 只从 `config.json` 取目标 | 代码路径唯一；调度类动作留给 Phase 4 Worker |

## 9. 已知限制

- **真机脱机 / 联机的完整往返**需要在管理员会话里跑一次（本次只验证到「未提权必须失败」这一侧）；夜间闸门在 23:58 走的就是这条路径，可作为真实验收。
- `MSFT_StoragePool.GetPhysicalDisks` 在部分精简系统上不可用，此时存储池判定退化为「未探测」并告警，不会假装安全。
- 进程线索会命中大量系统进程（尤其系统盘），仅作辅助展示，不参与系统盘判定（系统盘已经被硬阻断）。
- `\QuietHDD\` 任务文件夹 ACL 仍然限制普通用户读取，baseline 里相关任务标 `exists=null`，需管理员重跑补全（Phase 3 处理）。
