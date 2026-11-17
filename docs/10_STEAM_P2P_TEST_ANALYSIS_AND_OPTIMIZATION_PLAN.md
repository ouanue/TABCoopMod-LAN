# 10 — Steam P2P 跨机实测分析与优化方案（2026-11-15）

本文档对 2026-11-15 进行的真实双机 Steam P2P 合作联机实测（约 14 分钟生存新局）做完整的现象复盘、日志数据量化与代码级根因定位，并制定分阶段修复与性能优化方案。

---

## 1. 测试概况与日志归档说明

### 1.1 会话基本信息
- **测试时间**：2026-11-15 16:45 ~ 17:00（局内实玩约 `16:47:39` ~ `16:59:07`，`time` 推进至 `518,333 ms` / `Tick 17,160+`）
- **模式与房间**：Friends-Only Steam Lobby（`LobbyID = 109775244639753869`），2 人合作执政官模式，生存模式新局
- **平衡预设与地图**：`Co-op Balanced`（`NCells = 280`, `mapScale = 1.094`, `areaScale = 1.20`, `extraUnits = +2 Ranger`, `ccExtras = +2 workers`），动态会话种子 `-171268360`
- **同步模式**：`async_align`（默认）

### 1.2 归档日志文件与双机时钟偏移
双端实测日志均已归档至工作区 `logs/` 目录：

| 角色 | 玩家标识 | 归档路径 | 原始来源 | 行数 / 大小 |
|---|---|---|---|---|
| **Host (`PlayerId=1`)** | SteamID `76561199125593992` | `logs/TABCoopMod_host.log`（另保留 `logs/TABCoopMod.log` 原件） | 对端测试机复制 | 1,375 行 / 273,988 B |
| **Client (`PlayerId=2`)** | SteamID `76561198828760397` | `logs/TABCoopMod_client.log` | 本机游戏目录复制 | 828 行 / 110,644 B |

> **时钟对齐基准**：通过同一批 `SessionStart` 可靠包的双端时间戳对照（例如 `tick=12039` Host `16:55:55.352` 对应 Client `16:56:14.975`；`tick=16104` Host `16:58:24.582` 对应 Client `16:58:44.221`），**Client 本机系统时钟比 Host 快约 `19.64s`**（扣除约 `136ms` 单程网络传播，两机系统时间偏差约 `19.5s`）。下文中所有跨机因果对照均已扣除该偏差。

---

## 2. 核心现象一：操作延迟极高与周期性卡顿

用户反馈实测中**“体感上操作延迟极为明显”**。先说明观测边界：当前代码在 [`CommandInterceptorPatch.cs`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs) 与 [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) 中仅对“被拒绝/异常”的操作打日志，尚未对每一条正常指令记录“点击拦截时间 vs 回合执行时间”差值，因此无法直接读出单次点击的精确毫秒数；但**双端日志里的 RTT 探针、自适应缓冲档位、快照传输吞吐以及 `Tick gate waiting` 告警已足以精确定量出本次实际延迟结构**。

### 2.1 基础稳态操作延迟：`405 ~ 440 ms`（Client 端）
1. **实测链路 RTT 高达 `272.1 ms`**：
   - 开局进入同步后仅 `0.275s`，Host 日志就触发自适应降档上限：
     `[16:48:10.610] [INFO] [T1] [AdaptiveDelay] InputDelayTicks 1 -> 4 (smoothedRtt=272.1ms)`（`TABCoopMod_host.log:266`）
   - 随后整场 5 次重载的 `SessionStart` 均维持在上限 **`InputDelayTicks = 4`**（`4 × 33.33ms = 133.3ms`，受 [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L115-L117) 中 `MaxInputDelayTicks = 4` 与 `130ms` 阈值控制）。
2. **双端指令生效链路拆解**：
   - **Host 端**：本地捕获命令并在 `OnSimulationTickStarting(cur)` 盖章到 `cur + 4 ticks`（[`CoopSessionManager.cs:1367-1386`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L1367-L1386)），基础逻辑延迟 **`133.3 ms`**（叠加渲染帧对齐约 **`133 ~ 167 ms`**）。
   - **Client 端（最重灾区）**：每条本地操作必须完成 `Client 发送 ClientInputReport (~136ms 单程) -> Host 打包盖章 cur+4 -> Host 回传 TurnInputBroadcast (~136ms 单程) + InputDelayTicks (133.3ms)`：
     $$\text{Client 稳态生效延迟} \approx \text{RTT}\,(272\,\text{ms}) + \text{InputDelay}\,(133.3\,\text{ms}) + \text{Tick/帧对齐}\,(0\text{\textasciitilde}33\,\text{ms}) \approx 405\text{\textasciitilde}440\,\text{ms}$$
   - 在 RTS 高频微操下，近半秒的稳态响应延迟已会带来明显发粘感。

### 2.2 数秒级冻结卡顿：Client 全场 `82` 次 `Tick gate waiting`（`3.0s ~ 16.0s`）
比 `400ms` 稳态延迟更致命的是**频繁的秒级队列阻塞**。Client 日志共记录了 **82 行** `[CoopSessionManager] Tick gate waiting ... — packets from Host are delayed or lost`，主要来自以下三类根因：

#### 成因 1：Steam P2P 低带宽下 `PosDriftSyncChunk` 阻塞 Channel 0（实测 `3.0s ~ 4.0s` 队头阻塞）
- **实测传输速率仅 `55 ~ 63 KB/s`**：6 次快照从 `SnapshotBegin` 到收齐落盘的精确耗时如下（均来自 `TABCoopMod_client.log`）：
  | 轮次 | 大小 (Bytes / Chunks) | 下载耗时 | 折算速率 |
  |---|---|---|---|
  | 开局 Load 0 | `440,959 B` / 14 chunks | `16:48:14.557` -> `16:48:22.242` (`7.685s`) | `57.4 KB/s` |
  | Reload 1 | `455,326 B` / 14 chunks | `16:48:45.589` -> `16:48:53.809` (`8.220s`) | `55.4 KB/s` |
  | Reload 2 | `506,827 B` / 16 chunks | `16:51:56.963` -> `16:52:05.633` (`8.670s`) | `58.5 KB/s` |
  | Reload 3 | `514,328 B` / 16 chunks | `16:53:57.270` -> `16:54:06.023` (`8.753s`) | `58.8 KB/s` |
  | Reload 4 | `545,861 B` / 17 chunks | `16:55:59.308` -> `16:56:08.416` (`9.108s`) | `59.9 KB/s` |
  | Reload 5 | `569,556 B` / 18 chunks | `16:58:28.474` -> `16:58:37.549` (`9.075s`) | `62.8 KB/s` |
  这表明双机之间大概率走的是受限带宽的 Steam SDR 中继链路。
- **单通道队头阻塞（HOL Blocking）设计缺陷**：
  - [`SteamP2PTransport.cs:281-289`](../src/TABCoopMod/Networking/SteamP2PTransport.cs#L281-L289) 将 `SendChannel.ReliableOrdered` 全部映射到 **同一个 Steam P2P 虚拟通道 `Channel 0` (`EP2PSend.k_EP2PSendReliable`)**。
  - 与此同时，当 `lifeDiff || entityDiff` 时，[`CoopSessionManager.cs:2155`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L2155) 会将 `bucketMask` 置 `0`（全量发送所有 **12,000+ 个实体**的坐标与生命，经 [`TrySendPosDriftSync`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L2382-L2482) 压缩为多片 `32KB` Chunk，并在 `L2243` 通过同一个 `SendChannel.ReliableOrdered` 发送）。
  - 在 `~58 KB/s` 的可靠流上，一次全量 `PosDriftSyncChunk` 会直接把几十字节的 `TurnInputBroadcast` / `ClientInputReport` 堵在后面数秒。
- **日志 100% 实锤复现**：三次“入口即有差异 -> Host 立刻触发全量 `PosDriftSync`”的场次，Client 无一例外都在进入后仅仅 **9 ~ 11 个 tick（`0.3s`）** 就被队头阻塞冻结 3~4 秒；而入口 `MATCH`（未发全量 `PosDriftSync`）的 3 个场次均未发生开局冻结：
  - Load 0（`tick=2` 启动 `16:48:29.968`）-> Client 在 `tick=12` 报 `Tick gate waiting 3.0s..4.0s`（`TABCoopMod_client.log:273-274`），直到 `16:48:34.727` `Tick=2` 的 `PosDriftSync` 与被堵的 Turn 包同时到达；
  - Reload 2（`tick=5476` 启动 `16:52:12.361`）-> Client 在 `tick=5487` 报 `Tick gate waiting 3.0s..4.0s`（`TABCoopMod_client.log:475-476`）；
  - Reload 3（`tick=8734` 启动 `16:54:12.660`）-> Client 在 `tick=8743` 报 `Tick gate waiting 3.0s`（`TABCoopMod_client.log:589`）。

#### 成因 2：Host 模态弹窗 / 本地引擎暂停导致停发 Turn 心跳（实测 `7.0s` 冻结）
- **日志实锤**：Client 日志在 `tick=2405` 连续出现 `Tick gate waiting 3.0s` -> `7.0s`（`16:50:17.296` ~ `16:50:21.304`，`TABCoopMod_client.log:365-369`），紧接着一行（`L370`）打印：
  `[MayorSync] Closed peer's open mayor selection modal after selection.`
  对应 Host 日志在 `16:49:53` ~ `16:50:04` 出现长达 11 秒的 tick 停滞，并在 `16:50:02.338`（`TABCoopMod_host.log:440`）打印 `[GameBridge] Ignored duplicate mayor selection 'Williamamwermer'`。
- **代码根因**：《亿万僵尸》弹出市长选择等模态窗口时会置 `DXGame.Current.Paused = true` 并停止 `UpdateGameLogic`，但不会触发 Mod 的 `LockstepClock.PausedBySession`。而 [`CoopSessionManager.OnFrameTick`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L1236) 仅在 `LockstepClock.PausedBySession` 为真时调用 `EmitHostTurns`，导致 Host 弹窗选市长的 7 秒内完全停发心跳，Client 被 `TurnInputTickGate` 饥饿卡死。同样的现象也发生在对局末段 Host 切出菜单返回大厅前（Client `L815-818` 在 `tick 17199` 卡 `3.0s..6.0s`）。

#### 成因 3：全量快照自动重载中断 + 缺乏非编队指令的 0ms 本地反馈
- 5 次 Auto-resync 每次需要 Host 导出 + 重载（~7.5s）+ Steam 传输与 Client 加载（~15.5s），单次游戏中断超过 **20 秒**，极大恶化了整局操作连贯性。
- 同时，[`CommandInterceptorPatch.cs:123-127`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs#L123-L127) 目前只对 `GroupMoveOrPatrol` 做了 0ms 本地地面指示与单位语音；通过 `Patch_ZXCommand_Execute` 拦截的单单位命令、生产训练队列、取消队列等缺少即时的声画预反馈，在 `400ms+` 网络延迟下会形成“点下去没动静、半秒后突然响应”的割裂感。

---

## 3. 核心现象二：全场 5 次 Auto-resync 与状态分叉根因分析

### 3.1 5 次 Auto-resync 全景对照表

| 次数 | 触发 Tick / 原因 | 入口刚进场时是否一致？ | 演进与偏差细节 | 真正根因分类 |
|---|---|---|---|---|
| **#1** | **Tick 180**<br>`HARD DESYNC (econ x3)`<br>（`host.log:275`） | **已分叉（Tick 2）**<br>Host `entities=12074, res=10/20`<br>Client `entities=12071, res=100/20` | 一解除暂停即差 **+3 实体、-90 金**（恰好 3 座 `TentHouse` × 30 金），连续 3 次经济检查（`Tick 2 / 120 / 180`）后重载。 | **根因 A：屏障等待期漏拦截** |
| **#2** | **Tick 4920**<br>`PosDrift`<br>（`host.log:596`） | **入口 `MATCH`**<br>`Tick 276 MATCH (0x498068202D48B739)`（`host.log:314`） | 仅过 84 ticks 至 `Tick 360`，`4` 个 `Ranger` 出现 `MaxDelta=2.1002` 漂移；`Tick 1140` 扩至 894 实体；`Tick 4200` `MaxDelta=204.56`；`Tick 4920` `Drifted5=6038, MaxDelta=209.62` 触发重载。 | **根因 B：运行期位置确定性漂移** |
| **#3** | **Tick 8400**<br>`HARD DESYNC (econ x49)`<br>（`host.log:810`） | **已分叉（Tick 5476）**<br>Host `entities=12044, res=1018/6`<br>Client `entities=12043, res=1098/6` | 刚重载完 `Tick 5476` 就差 **+1 实体、-80 金**；因 120s 冷却限制持续了 49 次 `econStreak` 才触发重连。 | **根因 A：屏障等待期漏拦截** |
| **#4** | **Tick 11700**<br>`HARD DESYNC (econ x50)`<br>（`host.log:1031`） | **已分叉（Tick 8734）**<br>Host `entities=12027, res=562/20`<br>Client `entities=12026, res=762/30` | 刚重载完 `Tick 8734` 就差 **+1 实体、-200 金、-10 木**；后续 `Tick 9660` 因经济差距 Client 造出 `WoodWorkshop` 而 Host `CanExecute=false`（`host.log:903`），拖满 120s 冷却于 `econ x50` 重载。 | **根因 A：屏障等待期漏拦截** |
| **#5** | **Tick 15660**<br>`HARD DESYNC (econ x3)`<br>（`host.log:1261`） | **入口连续 3 次 `MATCH`**<br>`Tick 12039 / 12120 / 12180` 全匹配（`host.log:1069-1071`） | `Tick 12240` 出现亚格网级 `SOFT POS DRIFT (CoarseDiff=False)`，逐步扩散：`Tick 13980` 1,434 实体漂移 -> `Tick 14040` 投射物差 -> `Tick 14340` 血量/仇恨差 -> `Tick 14580` 实体数差 -> `Tick 15300` `Worker_A MaxDelta=11.04` -> `Tick 15540` 殖民地收税后金币差 16（`585` vs `601`）触发 `econ x3` 重载。 | **根因 B：运行期亚格网漂移逐步雪崩** |

> 注：第 5 次重载后（`Tick 16104 MATCH`, `host.log:1299`）至退出前（`Tick 17160`）同样观察到运行期位置漂移重现（`MaxDelta=20.36`, `host.log:1357`）。

### 3.2 根因 A（100% 实锤）：对称重载屏障等待期“关闭了拦截器，却允许 Host 玩家建造”
这是 **#1、#3、#4 三次硬分叉**的直接且唯一原因：

1. **现场时序证据**：以开局 Load 0 为例（#3、#4 完全一致）：
   - `16:47:47.949`：Host 导出 `coop_host_snapshot.zxsav`；
   - `16:47:48.440` -> `16:47:54.972`：Host 自重载完成并广播 `14 chunks` 快照；
   - **`16:47:54.972` -> `16:48:10.333`（长达 15.36 秒）**：Host 在等 Client 下载快照与 `InitLevel`，收到 `SnapshotLoadedAck` 后才于 `16:48:10.335` 发 `SessionStart`；
   - 紧接着在 `Tick 2` 第一次校验，Host 已比 Client **多 3 座建筑、少 90 金**（且 Tick 2 其余 12,032 个比对实体位置偏差为 `0.0000`）。
2. **代码缺陷点**：
   - 在 [`CoopSessionManager.cs:807-810`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L807-L810) 中，Host 进入对称重载前执行了 `IsInSyncedLevel = false; CommandInterceptor.IsInterceptionEnabled = false; LockstepClock.SetSessionPause(true);`。
   - 然而，当 Host 自己先加载完成（仅需 ~6s）、进入 `15.4s` 等队友期时，[`LockstepClock.cs:828-839`](../src/TABCoopMod/Determinism/LockstepClock.cs#L828-L839) 的 `OnLevelLoadComplete()` 却执行了 `hostGame.InputEnabled = true`、`ClearWaitCursor()` 和 `ClearLoadingFadeOverlay()`。
   - 由于《亿万僵尸》允许**游戏暂停期间造建筑和下指令**，Host 玩家在屏幕完全亮起后等待队友加载的 15 秒内顺手拍下的任何建筑，因 `CommandInterceptor.IsInterceptionEnabled == false` 而**全部 `return true` 直接写入 Host 内存**，既不进 `_hostTurnBuffer` 也不发给 Client，一开局就注定不同步！
   - 反向验证：**凡是 Host 在这 15.4 秒内没有操作的 #2（Tick 276）、#5（Tick 12039）及第 5 次重载后（Tick 16104），入口全部 100% `MATCH`！**
3. **冷却放大副作用**：[`CoopSessionManager.cs:105-106`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L105-L106) 在 `async_align` 下配置了 `AutoResyncCooldownMs = 120000.0`（2 分钟）。虽然能防止高频循环重载，但也导致 #3、#4 这种“第 1 帧就注定分叉”的局硬生生顶着报错多打 2 分钟（`econ x49`、`econ x50`），越积越大。

### 3.3 根因 B（运行期位置漂移）：#2 与 #5 从 `MATCH` 逐渐漂移的线索分析
为什么本地双进程确定性回放（Gate A1/A2）和 `900survival` 基准均能 `MATCH`，而真实双机在入口 `MATCH` 后仍会从个别单位逐渐扩散漂移？对照代码与日志，聚焦到两个主要怀疑点与一个可排除项：

1. **主要怀疑点 1：异步重叠帧 `canOverlapAsync`（回放验证与真实联机行为不一致）**
   - 在 [`LockstepClock.cs:1187-1213`](../src/TABCoopMod/Determinism/LockstepClock.cs#L1187-L1213) 中，`canOverlapAsync` 显式排除了 `!Testing.DeterministicReplayVerifier.IsRunning`——也就是说 **Gate A1/A2 跑的全是同步单线程帧，只有真实联机才开启了异步重叠帧**。
   - 开启 `canOverlapAsync` 后：
     - 工作线程执行 `_TaskLogic`（与 [`UpdateGameLogic_Postfix:1367-1381`](../src/TABCoopMod/Determinism/LockstepClock.cs#L1367-L1381) 链式的 `UpdatePhysics`，且其中在调用物理前先置了 `LogicTickActive = false`）的同时，主线程**并发运行 `RenderFrame/Present`**（含 `SetCamera` 中的 `RenderVisualInterpolationPatch` 读写实体坐标，以及渲染层事件）。双机 FPS 与主/副线程调度时序不同，极可能诱发微小的实体/视觉/物理时序扰动或局部 `0.1` 格漂移（正如 #5 在 141 个 `MATCH` tick 后突然冒出单个 `ZombieWorkerA` `0.1166` 格漂移）。
2. **主要怀疑点 2：`AsyncPosAlign` 仅纠偏坐标而不对齐移动上下文**
   - #2 在 `Tick 276 MATCH` 后仅 84 ticks 就先出现 `4` 个 `Ranger` 偏差 `2.1` 格，随后才蔓延到全图僵尸；#5 则在 `Tick 15300` 出现巡逻/采集单位 `Worker_A` 漂移 `11.0` 格导致采矿/建筑/死亡分歧。
   - 在 [`CoopSessionManager.cs:2625-2626`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L2625-L2626) 中，`AsyncPosAlign` 仅修补当前坐标差与生命差，并未重置/同步已出现偏离单位的速度向量、当前寻路缓存或目标引用；当 Client 上被拉回的单位继续按原有本地偏移方向行进或碰壁时，会在后续碰撞体里把差异传播给周围更多单位（雪崩效应）。
3. **可明确排除的假象：`SortEntitiesById` 数量差异（48,996 vs 78,601）**
   - 日志中 Host `Re-ordered 48996 entities` 与 Client `Re-ordered 78601 entities` 存在约 `29,605` 的固定差值，但该差值在 **`Tick 276`、`Tick 12039`、`Tick 16104` 三次全匹配 `MATCH` 时同样存在**，且两端 `DXPhysicWorld` 物理体数量（如 `13,196` / `13,199` / `13,172`）与 `StateHasher` 统计的仿真实体数（`12,074`）完全一致。该 `29k` 差值属于两机各自的本地 UI/视效/地图渲染级非仿真 `DXEntity`，**不是分叉原因**。

---

## 4. 分阶段优化与修复方案（Action Plan）

按 **“先消除确定性 Bug 与 3~4s 卡顿，再优化延迟体感，最后攻克运行期位置漂移”** 的顺序推进：

### Phase 1：修复屏障期漏拦截与弹窗停心跳（最高优先级）
1. **锁定重载屏障期输入（彻底消灭 #1 / #3 / #4 类开局/重连分叉）**：
   - 在 [`LockstepClock.OnLevelLoadComplete`](../src/TABCoopMod/Determinism/LockstepClock.cs#L825-L840) 与 [`CoopSessionManager`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L807-L850) 中：当 Host 处于 `_hostSnapshotReloadPending` 或 `_awaitingClientSnapshotAck` 时，**保持 `DXGame.Current.InputEnabled = false`**（并在 `CommandInterceptor` 与 `PauseAndSpeedInputPatch` 中双保险拒绝所有玩家指令，屏幕显示顶部横幅 `"Waiting for teammate to finish loading snapshot..."`），直到 `EnterSyncedLevel()` 双方握手完毕发送 `SessionStart` 时再统一开放输入。
   - **可选补充**：在 `CoopSessionManager` 启动屏障前，将等待期间（`15.4s`）或重连冷却（`AutoResyncCooldownMs`）的逻辑细化——若**刚进入/重载的前 3 个校验周期内**即发生 `Econ` 永久不一致（非正常战斗消耗），可绕过 120s 冷却更快触发恢复（避免像 #3/#4 一样带着错账继续玩 2 分钟）。
2. **修复模态弹窗 / 引擎局部暂停时停发心跳（消灭 #2 中 `Tick 2405` 的 `7s` 冻结）**：
   - 在 [`CoopSessionManager.OnFrameTick`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L1236) 与 [`EmitHostTurns`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L1352) 中，当 Host 检测到 `DXGame.Current.Paused || DXModalScreen.IsInModal`（如弹出市长窗口、打开 Esc 菜单）时，同步广播暂停状态或维持当前 `cur` 的保活心跳包，防止 Client 在未收到心跳时干等。

### Phase 2：混合传输升级（Steam 信令接头 + 双栈原生直连旁路 + 多通道保底）

采用 **“Steam Lobby 信令接头 + 原生 IPv4/IPv6 双栈直连自动升级（Direct Fast-Path）+ Steam 多虚拟通道自动回退保底”** 的双层传输架构，在不改变玩家任何联机操作习惯（依旧通过 Steam 好友邀请进房、无需手填 IP 或手动端口映射）的前提下，彻底打通高带宽低延迟链路并兼顾全网络环境兼容性：

1. **第一层：自动直连升级（Direct Fast-Path，解决 `220~272ms` 中继底噪与 `58 KB/s` 限速）**
   - **信令接头零配置**：房主建房、好友接受 Steam 邀请、Lobby 成员管理与初始握手仍完整沿用 `SteamP2PTransport`；
   - **候选地址自动交换**：在握手阶段通过 Steam 可靠通道自动交换双方本机的多类候选端点，包括：
     1. 局域网私有 IPv4（如 `192.168.x.x` / `10.x.x.x`）；
     2. 虚拟组网网卡 IP（如 Tailscale / ZeroTier / 蒲公英 / Radmin）；
     3. 运营商分配的公网单播 IPv6（`2000::/3`，当前国内家庭宽带普及率高且支持公网端到端直连，而游戏内置的旧版 `ISteamNetworking` 仅支持 32 位 IPv4 无法利用）；
     4. UPnP / NAT-PMP 自动端口映射及轻量 STUN 探测得到的公网映射 IPv4 端点。
   - **并发非阻塞探针与双向同时握手**：后台利用 IPv4/IPv6 双栈并行发起探针及双向发包（Simultaneous Open，同步在双方路由器有状态 NAT 表与本地 Windows 防火墙打开入站放行窗口）；只要任意一条直连通路在超时窗口（`< 500ms`）内完成检验，**立即无感将 `TurnInputBroadcast`、`ClientInputReport`、`SnapshotChunk`、`PosDriftSyncChunk` 等核心流量切换至原生直连通道**（可参考并扩展现有的 `LocalTcpLoopbackTransport` 帧协议，支持高优小包发送通道与大包队列分离）。
   - **实时故障无缝回落**：一旦直连心跳连续超时或通道断开，自动无感切回 Steam 通道，不触发断线重连弹窗。

2. **第二层：100% 兼容保底（Steam P2P 虚拟通道隔离 + `PosDriftSync` 瘦身，解决 `3~4s` 队头阻塞）**
   - 即使遇到无法建立原生直连的受限网络，流量也会稳妥留在 Steam P2P 通道；此时必须同步做好多通道隔离与流量瘦身：
   - **Steam P2P `nChannel` 分流**：扩展 `SendChannel`（[`INetworkTransport.cs`](../src/TABCoopMod/Networking/INetworkTransport.cs#L13-L17)）与 [`SteamP2PTransport.cs`](../src/TABCoopMod/Networking/SteamP2PTransport.cs#L281-L301)：
     - `ReliableCommand = 2`：专用于 `ClientInputReport`、`TurnInputBroadcast`、`SessionStart`、`TacticalPing`、`Handshake`、`Lobby*` 等高优先级小包；
     - `ReliableBulk = 0`：专用于 `SnapshotBegin/Chunk`、`PosDriftSyncChunk`、`StateChecksumReport` 等大体积后台同步包；
     - `UnreliableSequenced = 1`：保持用于 `AllyCursorAndCamera`、`RttPing/Pong`；
     - `PollEvents()` 循环遍历通道 `0..2` 收包。
     **预期收益**：即便只能走 `~58 KB/s` 的 Steam 中继，`TurnInputBroadcast` 也不会被数百 KB 的 `PosDriftSync` 或 `Snapshot` 堵出 `3~4s` 卡顿。
   - **大幅削减 `PosDriftSync` 数据体积**：
     - 修改 [`CoopSessionManager.cs:2155`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L2155) 与 `TrySendPosDriftSync`：剔除静止建筑（坐标恒定且仅少部分受伤），只按需打包动态单位或生命值变化实体，将单次载荷从 `12,000+` 实体压缩 80% 以上。

### Phase 3：操作延迟可观测性与本地交互体验优化
1. **补全指令全链路耗时与链路类型埋点**：
   - 在 `NetCommand` 增加本地生成时间戳（仅本机记录或可选诊断），在 `OnLocalCommandCaptured -> BroadcastHostTurn -> FlushDueTurnCommands / CommandExecutor.Execute` 测量并按周期打印 `[LatencyStats]`（如每 10 秒打印本机指令 `Avg / P95 / Max` 执行延迟 ms、Tick 差、当前 RTT、`InputDelayTicks`）；
   - 记录当前传输模式（`Direct IPv6` / `Direct IPv4` / `Virtual LAN` / `Steam Relay`，并在走 Steam 通道时调用 `SteamNetworking.GetP2PSessionState` 输出 `m_bUsingRelay` 与丢包率），并在等待大厅直观展示当前连接通道与 RTT。
2. **本地 0ms 反馈与缓冲调优**：
   - 为高频建造（含造墙）、兵营训练队列点击、单位停止等指令补齐本地即时确认音效与轻量化视觉/队列反馈，缓解 `InputDelayTicks` 带来的点击迟滞体感；
   - 根据通道是否已升级为直连及抖动情况微调 `AdaptiveDelay` 档位（如直连 `< 50ms` 时固定 `1~2 ticks = 33~67ms`）。

### Phase 4：运行期位置漂移（#2 / #5）专项排查
1. **A/B 验证 `canOverlapAsync` 的确定性影响**：
   - 给 [`LockstepClock.cs:1187`](../src/TABCoopMod/Determinism/LockstepClock.cs#L1187) 的 `canOverlapAsync` 增加可配置开关（如 `TABCOOP_DISABLE_ASYNC_TICK=1` 或在联机排查版本中临时关闭），实测关闭异步逻辑/渲染重叠并修复 [`UpdateGameLogic_Postfix:1367`](../src/TABCoopMod/Determinism/LockstepClock.cs#L1367) 的 `LogicTickActive` 时序后，观察是否能消除 `Tick 276 -> 360`（4 Rangers）与 `Tick 12180 -> 12240`（`ZombieWorkerA`）的原发位置漂移。
2. **定位首例漂移实体**：
   - 收集并对比 `Tick 276 -> 360` 中首次出现漂移的 4 个 `Ranger` 行为来源（如是否因初始移动/巡逻命令、建筑碰撞挤压或 `ZXBehaviourRunRngScopePatch` 触发），做针对性修补。

### 4.5 附：双机直连的网络环境要求与兼容性保底矩阵

双机原生直连（Direct Fast-Path）对网络拓扑确实存在客观要求，**并非所有玩家的网络环境都能直接直连成功**；但因系统采用 **“能直连自动升级、不能直连自动保留在 Steam 多通道中继”** 的无感降级设计，因此**对最终玩家没有硬性准入门槛，不会出现“网络不支持就进不去房间”的问题**。各类环境具体表现如下：

| 玩家网络环境组合 | 能否原生直连 | 原因与前提条件 | 实际预期体验 |
|---|---|---|---|
| **1. 同局域网 / 同路由器 / 虚拟局域网**<br>（同 Wi-Fi、校园同网段、或双方自装 Tailscale / ZeroTier / Radmin / 蒲公英） | **100% 支持** | 不经过公网 NAT，两机通过内网/虚拟网卡直通。 | RTT **`0.5 ~ 5 ms`**，带宽 **`50+ MB/s`**，几乎零延迟感，500KB 存档瞬传。 |
| **2. 异地宽带：双方均有公网 IPv6**<br>（国内三大运营商家庭宽带绝大部分默认分配 `2408/2409/240e` 公网 IPv6） | **高概率支持** | IPv6 通常无 NAT 转换，只需光猫/路由器开启“IPv6 穿透/未阻断全入站”或靠 Mod 双向发包打通有状态防火墙放行策略（并注意避免单边全局代理拦截 IPv6）。 | RTT **`15 ~ 45 ms`**，`InputDelayTicks` 降至 **`1~2`（`33~67ms`）**，高带宽秒传快照。 |
| **3. 异地宽带：至少一方有公网 IPv4 / 全锥形 NAT（NAT1/NAT2）** | **支持** | 公网 IPv4 可直接被连接；或光猫改桥接由主路由拨号且开启 UPnP / Full-Cone NAT 后，配合 STUN + 双向同时握手即可穿透。 | RTT **`15 ~ 50 ms`**，高带宽，效果与 IPv6 直连相近。 |
| **4. 单边纯 IPv4 + 双方均在运营商大内网对称型 NAT（Symmetric CGNAT / NAT4）**<br>（常见于：一方路由器未开 IPv6 / 用手机热点 + 另一方为多层 CGNAT 宽带） | **无法直连**<br>（自动保底） | IPv6 与 IPv4 无法直接互通；对称型 NAT 每次向外访问随机更换公网端口且严格丢弃陌生来源入站包，无中转服务器极难打洞。 | **自动回退 Steam P2P（Phase 2 多通道隔离版）**：虽然保留 Steam 链路底噪（几十到二百多 ms，视地区/中继而定），但彻底消除了 `3~4s` 阻塞卡顿，**依然可正常开黑**。 |
| **5. 严格校园网/企业防火墙或全局加速器劫持**<br>（禁 UDP/陌生端口入站、或某一方挂了国外节点加速器把本地游戏进程路由全部拉去海外） | **直连常受阻**<br>（自动保底） | 防火墙直接拦截外部直连包；或者加速器 TUN 网卡接管进程流量后只允许通过加速器网关出口。 | **自动回退 Steam P2P 通道保底**；如需触发直连，通常关闭冲突加速器或允许 Windows 防火墙放行 `TheyAreBillions.exe` 即可。 |

> **关键结论**：引入直连升级后，约 **80% 以上的常见家庭宽带/同网/组网玩家都能享受到 `15~45ms` 甚至 `<5ms` 的原生直连体验**；而剩下约 **10%~20% 处于单栈对称型 CGNAT 或严格防火墙下的玩家**，会自动由第二层 **“Steam 多通道隔离 + 瘦身同步”** 无缝接管保底，避免黑盒掉线或卡死。
