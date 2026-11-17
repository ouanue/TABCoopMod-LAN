# 08 — 经典 RTS 联机同步机制对比与后续可复用优化评估

> **整理日期**: 2026-11-14
> **定位**: 本文档归档关于“传统多人 RTS 同步方案、低延迟体感原理、与《亿万僵尸》(TAB) 底层引擎差异对比及后续可复用改造工程量评估”的讨论结论，作为后续演进排期的参考备忘。

---

## 1. 经典老牌 RTS 多人游戏的同步核心与“无延迟体感”原理

### 1.1 Deterministic Lockstep（确定性指令帧同步）
从《帝国时代 1/2》《星际争霸 1》《魔兽争霸 3》到《星际争霸 2》《最高指挥官》，同屏大量单位的 RTS 普遍采用确定性帧同步：
1. **只同步玩家按键指令，不同步全图状态**：无论同屏有多少单位或子弹，网络每秒仅传输几 KB 的输入指令（如 `"P1 预约在 SimTick N 让单位 [...] 移动到 (x, y)"`）。
2. **双层时钟（Simulation Tick vs Communication Turn）**：
   - 逻辑层以固定步长（例如 `10~22.4 Hz`）推进 `SimTick`；
   - 当前本地捕获的操作会附加 `InputDelay` 排期到未来某个 `SimTick` 再统一结算，从而吸收网络往返等待。

### 1.2 为什么存在 `80~150ms` 网络延迟仍感觉跟手？
在确定性前提下，操作真实生效必然滞后于按键瞬间。老牌 RTS 之所以让人体感顺滑，核心依靠 **4 项视图与输入解耦设计**：
1. **视图/UI 与逻辑帧 100% 隔离**：镜头移动、框选单位、切换编队、建造预览虚影全部运行在 `60~144Hz` 本地渲染层，不走网络也不等逻辑 tick。
2. **0ms 本地视听确认先行**：右键发出移动指令瞬间立刻在地面播放点击指示器与单位回应语音，给大脑提供“指令已确认”的即时心理反馈；单位实际在 `100ms` 后迈步在俯视角 RTS 中几乎不可察觉。
3. **渲染帧插值（Render Interpolation）**：逻辑帧离散推进，渲染帧按时间比例线性插值刚体位置，保障高刷观感。
4. **本地挂起态预扣与幽灵占位（Pending Footprint）**：指令发出到回合落盘的短暂窗口内，UI 层维护一份待确认占位与预扣费用表，防止连点造成重叠透支。

---

## 2. 《亿万僵尸》单机引擎差异、实机问题根因与现状对比

《亿万僵尸》（`TheyAreBillions.exe` v1.0.14.29）自研引擎 `DXVision` 按纯单机思路构建，且受限于 **Eazfuscator 反篡改（绝不修改磁盘 EXE/DLL 字节）**，只能通过 `AppDomainManager` + 54 类 Harmony 内存补丁改造为双端联机。实机测试中遇到的各类典型问题均源于底层单机假设：

| 问题现象 | 原生多人 RTS 设计 | 《亿万僵尸》单机引擎根因 | 本项目当前解决方式与核心落点 |
| :--- | :--- | :--- | :--- |
| **客机位置修正 / 飘移** | 定点数网格逻辑、无第三方浮点物理 | 非托管 C++ 浮点物理引擎（Chipmunk2D / `DXPhysicsNative`）+ 原生异步线程 + 共享行为 RNG，微差被游荡僵尸消耗共享 RNG 放大 | ① 物理与寻路收归确定性单线程；<br>② `ZXBehaviourRunRngScopePatch`（`DynamicMethod TABCoop_GetBehavOwnerEntityId`）将 `ZXBehaviour.Run()` 接入按 `(SyncedRandomSeed, SimTick, Entity.ID, drawIdx)` 的无状态 SplitMix64 序列；<br>③ `CoopSessionManager` 在 `async_align` 下分离 `2s` 血量/击杀同步与 `8s` 软位置对齐（单实体 `180 tick` 冷却去抖，死区 `MinAsyncPosSnapCells = 0.50f`，大偏差阈值 `maxDelta >= 3.5f \|\| rmsDelta >= 1.2f`）并结合速度阻尼。 |
| **移动视角卡顿** | 渲染与逻辑解耦，相机平滑基于帧时间 | 后期近 2 万实体单 tick 耗时约 `28~33ms`，同步等待阻塞渲染；且 `OnSetCamera` 硬限制单帧位移 `0.7f` | 启用单 tick 异步流水线（渲染期重叠推进 `_TaskLogic`）、`RenderVisualInterpolationPatch` 帧间插值（兼 `VisualSnapExpireMs = 600.0` / `VisualSnapTauMs = 180.0` 纠偏平滑衰减）与 `SmoothCameraPanPatch`。 |
| **单位无法完成攻击动作** | 攻击前摇与冷却严格绑定逻辑 tick | 后台线程 `_TaskGameTime` 异步修改 `GameTime`，且单位动作读写渲染精灵 `CSprite` 帧进度 | `GameTimeIsolationPatch` 冻结非授权时间修改，`CSpritePullIsolationPatch` 与 `CMovableSpriteProgressSyncPatch` 将精灵进度对齐逻辑时钟。 |
| **连点重叠建造、无视资源透支** | UI 本地挂起占位 + 执行期二次校验 | 单机认为点击即落盘，且实际加入 `DXWorldGrid` 延迟到帧队列 `PostMethods` 执行，导致延迟窗口连点在真正生成时未相互阻塞 | ① 拦截层 `Patch_ZXCommand_Execute` 校验 `CanExecuteCommandOnEntity`/`CanPlaceBuildingAtTarget`，并用 `_recentLocalBuilds`（`350ms` / `8 ticks`）去重重叠面积；<br>② 回放层 `CommandExecutor.ExecutePlaceBuilding` 执行前复验资源与占格，建造后立即调用 `FlushStartFramePostMethods()` 刷新网格占位。 |
| **双人同领市长双倍奖励** | 共享状态带版本守卫，首达胜出 | 原版 `ZXMayor.Apply` 单机无已领幂等校对，双方先后点出两份奖励 | `MayorSyncPatch` + `MayorModalTrackerPatch` 结合 `mayorLevel` / `MayorStates` 幂等拦截，首个玩家生效后自动关闭对端弹窗。 |
| **联机新建游戏总是同一张图** | 新局生成独立对局种子后下发客机 | 并非强退引起。旧版固定 `SyncSeed = 20261109`，`StartHostSession` 与 `InitLevel` 前按固定种子重置 RNG | **✅ 已解决**：新增 `CoopSessionManager.RollNewGameSeed()`，非 `TABCOOP_TEST=1` 会话在建房、选新生存/自定义地图及退回大厅时生成动态种子并随快照/`HostSyncBarrier` 下发客机（仅自动化回放测试固定 `20261109`）。 |

---

## 3. 双端并发冲突与建造实测结论（`900survival.zxsav`，18,074 实体）

通过双开控制端（Host `27182` / Client `27183`）并发注入同回合冲突指令，已实测验证：
1. **同批单位同时反向移动**：Host 与 Client 同时对同一批 3 个单位下发反向 `move_units`，双方按一致的确定性回合顺序执行，后者在两端统一覆盖胜出并收敛到一致轨迹（例：`3773031557935333289` 两端均抵达 `(132.89, 114.81)`），不发生状态分裂。
2. **完全重叠坐标并发建造（`ZX.Entities.WallWood` `@(120.5, 134.5)`）**：双方同时下单，最终仅成功建造 **1 座**（`WallWoodDelta Host=1 Client=1`），木材（`20→17`）与金币（`799`）仅扣除 1 份，后序指令在双端同时记录 `Rejected PlaceBuilding`。
3. **不同坐标并发建造（`(122.5, 132.5)` vs `(122.5, 134.5)`）**：双方均建造成功，双端同步新增 **2 座**（`WallWoodDelta Host=2 Client=2`），木材（`14→8`）与金币（`769`）一致扣除 2 份。
4. **整体稳定性**：在两轮实测中分别取得连续 **28/28** 与 **22/22** 次 `StateChecksum` 全 `MATCH`、`0 ERROR`、`0 AsyncPosAlign` 强制瞬移。

---

## 4. 后续可复用 RTS 方法与工程量分级评估（供后续排期参考）

在不违反“禁改磁盘 EXE/DLL 字节”铁律的前提下，按性价比与可行性分为三个梯度：

### 梯度一：轻量高性价比改造（约 50 ~ 300 行，0.5 ~ 1.5 天）
完全停留在现有会话层与拦截/UI 覆盖层内，风险极低：
1. ~~**非测试模式新建游戏动态种子握手**~~（✅ **已完成**）：
   - 已接入 `CoopSessionManager.RollNewGameSeed()`：非 `TABCOOP_TEST=1` 会话下每次建房/开新图动态滚动 `CurrentSessionSeed`，已在双开测试实测相邻两次新开得到不同种子并正确同步。
2. ~~**本地待建幽灵虚影与窗口期资源预扣累计**~~（✅ **已完成**）：
   - 在 [GameBridge.cs](../src/TABCoopMod/Core/GameBridge.cs) 新增 `TryGetCommandResourceCost` 与 `CanAffordWithPending`（覆盖 `Gold/Wood/Stone/Iron/Oil` 及 `ZXLevelState` 剩余可用 `Energy/Food/Workers`）；
   - 在 [CommandInterceptorPatch.cs](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs) 对 `_recentLocalBuilds` 窗口内未确认本地建造累计预扣资源并拦截超支连建，同时在 [AllyOverlayRenderer.cs](../src/TABCoopMod/UI/AllyOverlayRenderer.cs) 通过 `DXIsometricGridObject.Current.IsometricMiddle` 即时绘制玩家色半透明占位边框，并在 [NetCommand.cs](../src/TABCoopMod/Commands/NetCommand.cs) `ExecutePlaceBuilding` 落地或超时（`1500ms / 45 ticks`）/切图时自动清理。
3. ~~**0ms 本地单位语音即时确认**~~（✅ **已完成**）：
   - 在 [GameBridge.cs](../src/TABCoopMod/Core/GameBridge.cs) 接入 `PlayUnitCommandVoice`（通过 `LockstepClock.EnterAudioContext/ExitAudioContext` 隔离 RNG，复用原生 `ZXSystemAudio` 的 `SoundsOnCommandGeneric` / `SoundsOnCommandAttack`）；
   - 在 `Patch_LevelSys_GroupMoveSelected`、`Patch_ZXCommand_Execute` 与 `Patch_LevelSys_OnSceneRightClick` 捕获当下 0ms 发声，并在 [CommandInterceptorPatch.cs](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs) 新增 `Patch_Audio_SuppressReplayUnitVoice`（配合 [LockstepClock.cs](../src/TABCoopMod/Determinism/LockstepClock.cs) `AudioRngIsolationPatch` 优先级）在 `CommandExecutor.IsReplayingCommand` 期间屏蔽 `UnitVoices` 重复播报（已通过 `GameBridge 0 missing` 与 `Harmony Classes OK=57, failed-tolerant=0` 启动验证；实机听感待人工桌面交互验收）。
4. ~~**自适应 `InputDelayTicks`（`1~4 ticks`）**~~（✅ **已完成**）：
   - 在 [INetworkTransport.cs](../src/TABCoopMod/Networking/INetworkTransport.cs) 新增 `RttPing (0x42)` / `RttPong (0x43)`；
   - 在 [CoopSessionManager.cs](../src/TABCoopMod/Sync/CoopSessionManager.cs) 将 `InputDelayTicks` 改为 `1..4` 动态调节：局内同步状态下 Host 每 `500ms` 探测 RTT（EMA 平滑并做毛刺截断 + 迟滞阈值 + `1.5s` 冷却），`EmitHostTurns` 在升档时自动补齐中间空心跳包、在降档时保持 `targetTick` 单调递进不回退。实机测试验证：`latency=90;jitter=15` 下自动升档 `1 -> 4 (smoothedRtt=262.3ms)`、`latency=0` 下于首帧波动（`109.1ms`）后自动降档 `3 -> 2 (smoothedRtt=53.2ms)`，期间 `wait=0.0ms` 且全部 `StateChecksum MATCH`。

### 梯度二：中等深度确定性强化（约 400 ~ 1,000 行，2 ~ 5 天）
用于进一步压低大规模尸潮混战时的软修正频率：
5. ~~**物理步末浮点坐标/速度量化截断**~~（✅ **已完成**）：
   - 在 [LockstepClock.cs](../src/TABCoopMod/Determinism/LockstepClock.cs) `UpdatePhysics_Exit` 中新增 `LockstepClock.QuantizePhysicsBodiesPostStep()`（可通过 [TestHarnessConfig.cs](../src/TABCoopMod/Testing/TestHarnessConfig.cs) `TABCOOP_PHYSICS_QUANT` 配置倍率，默认 `256` 即 `1/256 = 0.00390625` 格，`0` 可关闭用于 A/B 对照）：
     - 在每次有效物理步进（`DXPhysicWorld.NUpdates != __state`）结束后遍历活跃 `DXBodyType.Dynamic` 刚体，对有限的 `PhysicPosition`（并按 `DXWorldGrid.NCells` 做安全边界防越界夹紧）与 `PhysicVelocity` 使用 `MidpointRounding.ToEven` 量化到二进制精确浮点格点；
     - 仅在数值实际变化时写回 `PhysicPosition` / `PositionComputed` / `DXEntity.Position`（同步更新 `DXWorldGrid` 格缓存）以及 `PhysicVelocity` / `Velocity`，并在移动体上保留 `LastPositionComputed` / `LastPosition` 以确保 `RenderVisualInterpolationPatch` 帧间插值平滑（仅静止体首次吸附网格时同步 `Last*`）；
     - 实测：`1_Backup.zxsav` 120 tick 双进程回放 `identical`（`maxDx=0, maxDy=0`）；`1_Backup.zxsav` 60s `Chaos-1`（`latency=120;jitter=30;loss=0.02;disconnect=4000-5000;seed=42`）从原本 Tick `32940` 触发 `Pos` 漂移收敛为 **Tick `31904..33720` 连续 31/31 `MATCH`、`0 Auto-resync`、`0 ERROR`**（`quant≈0.25ms/p`）；`900survival.zxsav`（18,074 实体）密集群体汇聚与反向行军双端暂停同 tick 对照 `maxPeerDiff=0.0000`、`minPairDist=0.4617 / 0.1105`（未破坏碰撞分离）、10/10 `MATCH`、`wait=0.0ms`。
6. **战斗索敌目标 ID 同步锁定**（约 `400~600 行`）：
   - 绑定 `BData` 仇恨目标字段，在 `2s` 轻量状态同步包中附带处于接战态单位的 `TargetEntityId`，避免临界距离差异引发双方僵尸追向不同目标后越走越远。

### 梯度三：重度底层重构（10,000+ 行，不建议/不可行）
- **全引擎定点数化**：游戏主程序反篡改与 `DXVision.dll` / 非托管 `DXPhysicsNative` 的全套 `PointF`（32 位浮点）API 决定了无法在不改二进制的前提下全局替换定点数。
- **回滚净码（Rollback Netcode）**：`900survival` 等大地图单逻辑帧耗时已达 `28~33ms`，缺乏 `<1ms` 多帧状态快照/回退重算的算力空间，回滚只会加剧卡顿。
