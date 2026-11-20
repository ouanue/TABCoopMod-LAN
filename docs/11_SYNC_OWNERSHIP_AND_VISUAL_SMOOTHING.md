# 11 — 控制权差分对齐与单位移动视觉平滑优化分析

> **整理日期**: 2026-11-15（**2026-11-16 已完成代码落地与自动化联调回归**）  
> **定位**: 本文档归档关于“按最后操控者差分切换异步位置对齐方向”、“移动指令本机先行计算的边界与替代方案”以及“单位移动视觉帧率低/非连续感底层根因与渲染插值层修复方案”的技术讨论结论，以及对应的工程实现与实测结果。

---

## 1. 按最后指令人（`PlayerId`）差分对齐单位位置（✅ 已实装）

### 1.1 现状与痛点
- 在当前 `async_align` 模式下（见 [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) 与 [`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)），位置修正是固定的单向 **Host → Client**：主机定期下发偏差桶内实体的 `(Id, X, Y, Life)`，客机对超过阈值（移动中玩家单位 `1.80f` 格、静止玩家单位 `0.50f` 格）的单位做阻尼拉回与 `600ms` 视觉平滑衰减。
- 在执政官模式（共享阵营、独立操作）下，如果客机正在高频微操一队游侠，而两端因物理微小差异发生漂移时，主机向客机拉回会让客机操作者感受到非预期的单位位移或手感被扯回。

### 1.2 可行性与改造方案（难度：中等偏小，约 150~250 行）
在 [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs) 的 `FlushDueTurnCommands` 中，所有端都在同一 `TargetSimTick` 按完全一致的顺序执行 `NetCommand`，且每条指令天然携带 `cmd.PlayerId` 与 `cmd.ExecutorEntityIds`。因此可以基于以下三步实现**按操控者差分对齐**：

1. **维护确定性控制权表**：
   - 在 `FlushDueTurnCommands` 执行单位移动/攻击等指令时，两端同步更新 `Dictionary<ulong, (byte PlayerId, int LastOrderTick)>`（仅追踪 `IsPlayerEntity` 的可移动单位）。
2. **客机自控单位免拉回保护**：
   - 客机收到主机的 `PosDriftSyncChunk` 时，若某单位当前处于自己名下的有效控制租约期内，**忽略主机的常规软位置修正**（仅当偏差达到极端阈值如 `>= 3.5` 格时才保留兜底），从而使客机正在操作的单位 **0 拉回**。
3. **轻量反向对齐（Client → Host）**：
   - 全图 1~3 万实体绝大部分为僵尸和建筑，处于“客机正在操控且移动中”的玩家单位通常只有数个到数十个。
   - 客机每 `2s` 发送 `StateChecksumReport` 时，可顺带附加上这部分自控移动单位在当前校验 Tick 的 `(Id, X, Y)`（仅数百字节）；主机收到后以相同的阻尼系数与 `RenderVisualInterpolationPatch` 视觉平滑将主机上的对应单位对齐向客机（3 人局时再由主机中继给第三人）。

### 1.3 潜在副作用与必须设置的护栏

| 潜在副作用 | 根因分析 | 对应护栏设计 |
| :--- | :--- | :--- |
| **1. 人尸贴脸肉搏时的刚体互穿/弹射** | 玩家单位按客机对齐，但周围僵尸群仍按主机对齐。贴脸卡位时若主机把客机僵尸往左拉、客机同时把主机游侠往右拉，可能瞬间重叠进同一碰撞体并在下一物理步被 Chipmunk2D 剧烈弹开。 | 反向对齐必须沿用现有阻尼（`< 2.2` 格乘 `0.60~0.75`）与 `EntityPosRealignCooldownTicks = 180` 单实体冷却去抖，禁止贴脸推挤时直接硬瞬移。 |
| **2. 血量/受击判定的“隔空挨刀”视觉落差** | 血量与生死结算（`LifeFixes`）**绝不能分片交给客机**，必须保持 Host 单一权威。当位置听客机、扣血听主机时，若两端存在 `0.8` 格偏差，可能出现客机看似已拉出攻击范围却在 `LifeFixes` 同步后掉血的现象。 | 借助每 `2s`（60 ticks）随 `StateChecksumReport` 高频捎带自控单位坐标，将主客机上该小队的位置差持续压制在 `0.2~0.3` 格以内。 |
| **3. 控制权永久滞留（所有权僵尸化）** | 若客机曾框选过全军，单位永久归客机对齐，后续主机接管或单位闲置时反而会被客机旧状态拉扯。 | 控制权必须设置**租约 TTL**（例如最后下令后 `8~10s`，或单位停止移动进入待机超过 `3s` 后自动失效），到期自动将对齐基准归还主机。 |
| **4. 双人同时抢控同一单位的冲突** | 多人同回合或相邻回合对同一批单位下令。 | `FlushDueTurnCommands` 在全端执行顺序 100% 一致，天然满足“最后一条落盘指令的 `PlayerId` 胜出”，无需额外协商协议。 |

---

## 2. 单位移动“本机先行计算”的可行性与替代方案

### 2.1 现有框架的延迟来源
- 当前 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 已实装单 tick 异步流水线与 `RenderVisualInterpolationPatch` 帧间插值，[`CommandInterceptorPatch.cs`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs) 已实装 0ms 本地地面指示器与 0ms 单位语音即时确认。
- 玩家感知到的“操作略滞后”，本质是确定性锁步的 `InputDelayTicks`（`1~4 ticks`，即 `33~133ms`）排期等待，而非渲染帧率本身。

### 2.2 为什么严禁在逻辑/物理层做 0ms 抢跑移动？
违反项目铁律 #5（任何改变游戏世界的操作都必须走 `NetCommand`），直接在逻辑层不等 `TargetSimTick` 让本机单位先行移动会导致 **3 个系统性副作用**：
1. **制造必然的永久性相位超前**：本机 `Tick 100` 点击即走，权威包在 `Tick 103` 落盘，本机单位会永远超前 3 帧（约 `0.4~0.6` 格），导致 `StateChecksum` 从大部分 `MATCH` 退化为只要微操就永久 `DESYNC`。
2. **权威回合包回传后的二次执行抽搐**：`Tick 103` 权威包到达时，若再次执行则重置寻路/分散目标点造成顿挫；若跳过则一旦有队友指令插入或地形变化，两端指令状态机立即分叉。
3. **全图寻路网格与随机数流级联污染**：单位移动会实时改写 `DXWorldGrid` 占格、触发 Chipmunk2D 推挤周围刚体，并改变进入僵尸索敌圈的帧号，导致周围未操作的单位和尸潮同步失步。

### 2.3 零副作用替代方案：0ms 本地反馈与“视觉位移预演”实测回退结论（❌ 视觉前移已撤除）
- **保留零副作用 0ms 本地反馈**：[`CommandInterceptorPatch.cs`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs) 保留 0ms 本地地面指示圈（`ShowGroundIndicatorForCommand`）与 0ms 单位语音确认（`PlayUnitCommandVoice`），在不触碰任何逻辑/物理状态的前提下提供即时操作回馈。
- **纯渲染位移预演（`TriggerMoveAnticipation`）实机回退原因**：
  - 本轮曾实装在等待 `TargetSimTick` + 首物理步期间对 `SceneObject` 施加最多 `0.28~0.42` 格前向位移并在起步后指数衰减交接的方案；
  - 实机验证表明：**无论在到达 `ExpectedExecTick` 提前衰减，还是推迟到首物理步再以 `τ=70ms` 衰减，都会出现“向指令方向微移 → 退回/缩回原位 → 再真正起步”的回扯感**——数学根因在于 `PeakOffsetCells / τ ≈ 4~6 格/秒` 超过多数单位刚起步的实际物理速度（以及 Client 在高 RTT/Chaos `cmdAvg=523~592ms` 下超过最大等待保护窗口触发回缩，或首步绕障未达方向点积阈值导致到期归零）。
  - 因此当前版本已**彻底撤除** `TriggerMoveAnticipation` 假位移，保留纯物理帧间平滑插值与 0ms 地面光圈/语音回馈，确保单位静止态稳固、起步无任何反向回拉。

---

## 3. 单位移动视觉帧率低 / 非连续感的底层根因与修复方案（✅ 已实装）

> **与文档 08 目标 5 的协同说明**：[`docs/08_RTS_SYNC_COMPARISON_AND_FUTURE_OPTIMIZATIONS.md`](08_RTS_SYNC_COMPARISON_AND_FUTURE_OPTIMIZATIONS.md) 梯度二的目标 5（`UpdatePhysics_Exit` 中 `1/256` 格物理坐标/速度量化截断）已同步实装。在实现上，`LockstepClock.QuantizePhysicsBodiesPostStep()` 先于 `OnPhysicsStepCompleted()` 运行：对移动中的刚体保留 `LastPositionComputed` / `LastPosition` 并仅将当前帧端点 `PositionComputed` 对齐到 `1/256` 格点（约 `0.25px` 精度），因此全场景插值 `lerp(LastPositionComputed, PositionComputed, alpha)` 仍保持连续，不会引入额外阶梯感；且软对齐/反向对齐写回坐标同样经 `LockstepClock.QuantizeCoord` 对齐网格。

经过对 `DXVision.dll`（`DXGame` / `DXEntity` / `DXPhysicWorld` / `DXPhysicBody`）、`TheyAreBillions.exe`（`CSprite` / `CSelectable`）与 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 的对照排查，单位移动呈现“阶梯感/非连续”的根因来自以下 **4 个具体机制**：

### 3.1 底层物理本身为 12Hz，与 30Hz 逻辑帧呈 `2 / 3` 帧交替拍频
- 引擎中 30Hz 的逻辑帧（`33.33ms`）只施加移动推力，单位实际坐标由 `DXPhysicWorld.UpdateDefaultTime()` 以 **12Hz（每 `83.33ms`）** 步进 Chipmunk2D 计算并写回 `DXPhysicBody.PositionComputed`。
- 由于 `83.33ms / 33.33ms = 2.5`，确定性单线程步进时物理更新会以 **2 个逻辑 Tick（`66.7ms`）与 3 个逻辑 Tick（`100.0ms`）交替触发**，真实坐标每秒仅更新 12 次，高度依赖渲染插值补帧的稳定性。

### 3.2 `RenderVisualInterpolationPatch` 插值系数 `alpha` 的时序错位与平台期停顿
在 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 的 `StartFrame_CaptureMainThread()` 中：
```csharp
FlushPendingAsyncTick(DXGame.Current);          // 先结束异步逻辑+物理 -> 触发 OnPhysicsStepCompleted() 归零并计算 overshoot
RenderVisualInterpolationPatch.UpdateFrameAlpha(); // 后把刚刚流逝的上一帧 dtMs 加到新物理步上
```
这造成了两个直接的视觉顿挫：
1. **首帧跳变**：`OnPhysicsStepCompleted()` 计算 `overshoot` 时尚未计入本帧刚过去的 `dtMs`，导致 `overshoot` 几乎恒为 `0`；紧接着 `UpdateFrameAlpha()` 又把上一帧 `dtMs`（`16~33ms`）直接加到新物理步上，使新周期第 1 渲染帧直接从 `alpha ≈ 0.25~0.35` 起跳，跳过前半段过渡。
2. **末尾 `alpha = 1.0` 停顿**：在 3-Tick 物理间隔（`100ms`）或客机等待网络包抖动期间，`_accumPhysicsElapsedMs` 会提前触顶 `1.0`，单位在终点停顿 1~2 个渲染帧，待下一物理步完成后再突然向前滑行，形成“滑一下、停一下”的阶梯感。

### 3.3 脚下阴影（`CShadow`）、附加组件与等轴测 `Z` 深度未吃插值
- 当前 `RenderVisualInterpolationPatch` 仅挂钩了 `CSprite.#=zyeyG75rdugKq`（人物贴图）与 `CSelectable.#=zyeyG75rdugKq`（选中绿圈），在各自 Prefix 临时修改 `DXEntity._Position` 并在 Finalizer 立即还原。
- 因此在 `DXEntity.UpdateSceneObjectParallel()` 内的其他组件（尤其是紧贴单位脚底的阴影 `CShadow`）以及随后主线程 `DXEntity.UpdateSceneObject()` 计算的 `SceneObject.Z = GetZ()`，读到的依然是还原后的 **12Hz 离散坐标**。
- 人物贴图平滑滑动而脚底黑影按 12Hz 逐格跳跃，视觉叠加后会产生明显的抽帧感。

### 3.4 `GameTimeIsolationPatch` 将单位行走动画切帧锁定在 `<= 30Hz`
- 原版单机通过后台线程 `_TaskGameTime` 每 2ms 刷新 `DXGame.Current.GameTimeInt`，使 `CSprite` 的行走动画进度 `GameTimeInt - TimeAnimationStart` 随高刷渲染连续推进。
- 为保证确定性，[`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 中的 `GameTimeIsolationPatch` 锁定了 `GameTime` 仅在逻辑 Tick 步进时跳变 `+33.33ms`，导致渲染期读取的动画时间也呈 30Hz 阶梯跳变。

### 3.5 纯渲染插值层修复方案（不动同步协议与确定性物理）（✅ 已实装）
1. **扩大位置插值覆盖面至完整场景更新期**：
   - 因 `DXEntity.UpdateSceneObjectParallel` 含 `catch when (!DXGame.IsDebuggerAttached)` 过滤异常块无法直接 Harmony IL 重写，改由 [`SetCameraVisualPassPatch`](../src/TABCoopMod/Determinism/LockstepClock.cs)（`DXGame.SetCamera`）与 [`UpdateVisibleCellsVisualPassPatch`](../src/TABCoopMod/Determinism/LockstepClock.cs)（`DXWorldGrid.UpdateVisibleCells` Postfix）在 `DXHelper_Task.For` 场景更新前于主线程将活跃动态实体 `_Position` 批量投影为平滑插值坐标，并令 `DXPhysicBody.InterpolatedPosition` 在场景更新窗口内返回已投影坐标（避免多线程回写 `DXWorldGrid.SetNewCellFor`），随后在 `UpdateGameLogic_Prefix` / `SetCamera` Finalizer 中统一还原，使 `CSprite`、`CSelectable`、脚底阴影 `CShadow` 与 `UpdateSceneObject` Z 轴深度同步平滑。
2. **修正 `UpdateFrameAlpha` 与 `OnPhysicsStepCompleted` 时序并固定 `83.33ms` 时钟基准**：
   - 在 `StartFrame_CaptureMainThread()` 改为先执行 `RenderVisualInterpolationPatch.UpdateFrameAlpha()` 累加本帧 `dtMs * GameSpeed`，再执行 `FlushPendingAsyncTick()` 触发 `OnPhysicsStepCompleted()` 从累加器扣除固定 `MsPerPhysicsTick`（`83.3333ms`）保留精确 `overshoot` 余数，消除首帧跳变与 2/3-tick 交替拍频造成的 `alpha = 1.0` 平台期停顿。
3. **渲染期临时提供带插值余数的视觉 `GameTimeInt`**：
   - 随逻辑 tick 同步追踪 `_accumLogicElapsedMs`，在 `SetCamera` 纯渲染场景更新窗口内临时为 `DXGame.GameTime` 叠加 `[0, 32.6ms]` 渲染余数，并在 `ExitVisualSceneUpdate` 精确还原为 `_savedAuthGameTime`，保障高刷下的行走动画帧流畅度而不污染任何逻辑/物理 tick 确定性。

---

## 4. 实现落地与实测验证记录（2026-11-16）

- **代码落点**：
  - [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)：确定性控制权表 `_unitOwnership`（`OwnershipLeaseTicks=300` / `OwnershipIdleGraceTicks=90`）、客机自控保护（`SelfOwnedHostOverrideThreshold=3.5f`）、随 `StateChecksumReport` 捎带 `CollectClientOwnedActiveUnits`（复用 `dynamicPositions` 同 epoch 采样与主机 `_tickPosCache[rev.Tick]` 同 tick 对比，消除跨端 tick 相位差）及主机 `_incomingReverseAlignQueue`（`0.25f` 死区、`0.65f` 阻尼、`180` tick 冷却、`QuantizeCoord` 与 `RecordVisualSnapOffset`）。
  - [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) & [`CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)：全场景插值投影/还原、`StartFrame` 时序调整、固定 `83.33ms` 基准、视觉 `GameTime` 余数，以及退出返回主菜单期间 `EnterVisualSceneUpdate` 场景类型/加载守卫与 `Monitor.TryEnter(DXPhysicWorld.Sync)` 并发清理保护（并已撤除造成回扯的 `TriggerMoveAnticipation` 位移预演）。
- **实测结果**：
  - **注入与健康度**：Release `0 警告 0 错误`；`GameBridge 0 missing`，Harmony **`Classes OK=61, failed-tolerant=0`**。
  - **`1_Backup.zxsav` 120 tick 双进程回放**：`[compare] PASS: 120 ticks identical`，`common=2125, moved(>0.01)=0, maxDx=0, maxDy=0`（视觉时间余数与插值投影对确定性零污染）。
  - **`900survival.zxsav`（18,074 实体）正常环回双端轮控**：Client 与 Host 依次指挥 8 个可移动单位汇聚及反向行军，暂停对齐至同一 tick 采样：`Round 1 (H=C=4989) maxPeerDiff=0.0000, minPairDist=0.4518`、`Round 2 (H=C=5276) maxPeerDiff=0.0000, minPairDist=0.3441`、**9/9 `StateChecksum MATCH`**、`0 ERRORS`。
  - **`900survival.zxsav` 恶劣网络模拟（`ChaosTransport`: `latency=150;jitter=50;loss=0.05;seed=99`）**：
    - 轮次 A（`disconnect=25000-27000`，RTT `418~478ms`，`AdaptiveDelay` 自动升档 `1 -> 4 (smoothedRtt=646.0ms)`，累计丢包 Host `25`/Client `31`）：Host/Client 轮流下达 3 轮移动指令后，最终暂停同 tick `H=C=5689, maxPeerDiff=0.0000, minPairDist=0.3922`，**16/16 `MATCH`**、`0 DRIFT/DESYNC`、`0 ERRORS`、`wait=0.0~0.3ms`。
    - 轮次 B（`disconnect=45000-47500` 精准命中局内行军阶段 2.5 秒完全断连，RTT `408~538ms`，`cmdMax=2897.8ms`，累计丢包 Host `59`/Client `68`）：断连恢复后无崩溃或卡死，全程 **16/16 `MATCH`**、`0 DRIFT/DESYNC`、`0 ERRORS`、`wait=0.1~0.8ms`。
  - **2026-11-16 撤除预演 + 返回主菜单修复后双开 Chaos 回归（`1_Backup.zxsav` + `ChaosTransport`: `latency=80;jitter=20;loss=0.01;seed=7`）**：Release `0 警告 0 错误`，`GameBridge 0 missing`，Harmony **`Classes OK=61, failed-tolerant=0`**；客机发起 `move_units` → `cancel_current_order` → `move_units`（3 游侠，`cmdN=3, cmdAvg=383.2ms, rtt=252.2ms, delay=4t`）期间 **6/6 `StateChecksum MATCH`**（Tick `31904~32220`）；随后双端依次调用 `return_to_menu` 均稳定回到大厅（`HostAlive=True, ClientAlive=True`，`roomPhase=InLobby`），全程 `0 ERROR / 0 UnhandledException`。
  - **待人工桌面确认项**：`CShadow` / 动画序列帧在高刷显示器下的主观连续度，以及撤除位移预演后无“先往前挪再退回”的起步手感与局内退出回大厅稳定性。


---

## 5. 亚格位置漂移的微修正通道（`v6.4`，待真机验收）

### 5.1 为什么既有位置修正几乎不工作
- 真实双机日志（host `host_FINAL_2033.log` / client `client_FINAL_2234.log`，Build `2026-11-20 18:51:44`）显示：`[StateChecksum]` 的 `Pos`（`0.01` 格量化，`qz = (long)Math.Round(px * 100f)`）**从第一个 epoch 起就 100% 不同**，而同 tick 的 `phys`（`DXPhysicWorld.NUpdates`）、`time`（`GameTimeMs`）、`Life`/`Aggro`/`Proj`/`Prod`/`Resv`/`entities` 全部逐字符相同。⇒ 不是时钟偏、不是步数偏、也不是实体集合偏，纯粹是**同一个物理步内部两端算出的坐标不同**。
- 该差异属调度敏感的概率性残留：本机双进程 A/A 在**不限核**时从不漂移，但把两个进程的 `ProcessorAffinity` 倾斜（`2` 核 vs `20` 核）或用 `2` 核对 `2` 核后即复现同型漂移（`Pos` 先变、`Coarse` 后变、其余通道全同），与 [`DeterministicTaskPatch.cs`](../src/TABCoopMod/Determinism/DeterministicTaskPatch.cs) 头注释“A/A 大约一半的跑次会漂”一致。
- 修正通道的实际效率：6 段真实联机累计 `[AsyncPosAlign]` 报 **166** 次，`applied = 11726 / compared = 2083107`，即 **0.5629%**。逐条累加 `Drifted(>0.001, >0.01, >0.1, >0.5)` 得到 `228884 / 210393 / 155419 / 93307` ⇒ **59% 的漂移量落在 `[0.001, 0.5)` 格**，被 `MinAsyncPosSnapCells = 0.50f` 整段丢弃；即便进了列表，应用侧第二层 `GameBridge.GetEntityPositionSnapThreshold` 又要求 `0.50~1.80` 格，仍然过不去。两层叠加使得亚格漂移**永远不会被修正**，只能等 `Auto-resync` 全量重建快照。

### 5.2 实现
- **下限**：`MicroAlignFloorCells` = `TABCOOP_PRECISION_ALIGN_CELLS`（若设了正值）否则 `2 / TABCOOP_PHYSICS_QUANT`，默认 `2/256 = 0.0078f` 格。选 `2` 倍量化步长而不是 `1` 倍，是因为 `LockstepClock.QuantizeCoord` 用 `Math.Round(..., MidpointRounding.ToEven)` 把坐标吸附到 `1/256` 格点，恰好等于一步长的差值有被就近舍回原位的风险。
- **冷却**：`MicroAlignCooldownTicks = 24`（约 `0.8s`），远短于常规的 `EntityPosRealignCooldownTicks = 180`（`3s`）——微修正是亚格级、便宜且频繁的，用常规冷却会让它几乎无用。**两张冷却表分开维护**（`_lastEntityPosCorrectedTick` / `_lastEntityMicroCorrectedTick`），否则一个被微修过的单位漂到 `>=0.5` 格时会被 `180 tick` 冷却挡住，等于白拿一个它没挣来的三秒锁死。
- **无阻尼精确吸附**：微漂移带直接 `applyDx = d.Dx; applyDy = d.Dy`，不乘 `damping`。依据是 `dx = hs.X - cs.X` 本身就是**同一 tick** 的主客机真实差（主客两端各自用 `_tickPosCache.TryGetValue(buf.SimTick, …)` 取同 tick 快照），乘 `0.55~0.75` 只走一多半，留下的残差会被下一步物理重新放大，等于没修。
- **单列表**：收集侧仍只往 `deltas` 里加一份，应用侧按 `dist` 分流到微漂移带或常规带。两路列表对同一实体可能给出冲突的写入顺序，而应用侧本来就要重新测 `dist`，没有分路的必要。
- **护栏**：微漂移带里 `TryGetActiveUnitOwner` 命中本地玩家的单位**一律不动**——主机样本已经在网络上滞后一个 RTT，本地输入还没到主机，此时把本地单位拉向旧位置会直接对抗玩家的操作手感。自持单位只在常规带、且 `dist >= SelfOwnedHostOverrideThreshold = 3.5f` 时才允许兜底拉回。
- **预算**：`MicroAlignMaxUnitsPerReport = 2048`，防止大局（实测 `17272` 个 dynamic body）单次报告把所有实体都刷一遍导致卡帧；超预算的实体留到下一轮。
- **开关**：`TABCOOP_PRECISION_ALIGN`（默认开启，`=0` 精确回到 v6.3 行为）。日志新增 `[AsyncPosAlign] … micro=N` 计数，`PosDriftStatsReport` 回报的 `applied` 也把微修正计入。

### 5.3 验收状态（未完成，不可假设通过）
- 本机 A/A 对照（`2` 核 vs `20` 核、每跑 `200s`、`align-sweep.ps1`）：`align=0` 3 跑中 **1** 跑漂移（6 次 `TRANSIENT STATE DRIFT`，首漂 tick `44220`）；`align=1` 3 跑 **0** 漂移。但每跑只有 `0~11` 个 `StateChecksum` epoch，样本量远不足以定论，**只能作为方向性参考**。
- 真机双机验收尚未进行：需要两台都设 `TABCOOP_TRACE_FIRST_TICKS=2000`，再用 `cmptrace.ps1` 观察首个分歧 `rel` 是否明显后移或整体消失，并用 `TABCOOP_PRECISION_ALIGN=0` 做同存档同会话的对照轮。判定门槛是 `SOFT POS DRIFT` 比例与 `MaxDelta` 峰值显著下降且 `Exceeded=True` 不再出现——仅“这次没漂”不算通过（漂移本身是概率性的）。