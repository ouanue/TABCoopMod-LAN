# 06 — 当前项目状态、门禁验收报告与下一步计划 (Project Status)

> **最后更新**: 2026-11-17（**发布 `v0.1.5`**：修复客机后期被单 tick 异步上限锁死导致 3–29s 延迟漂移与暂停失效、新增主机对落后客机的实时 tick 反压流控、修复 `DirectFastPath` 包长/窗口/假死回退缺陷，以及 `SteamP2PTransport`/`LocalTcpLoopbackTransport` 玩家槽位回收复用）
> **最新增量**: 2026-11-21（**`v6.4` 亚格位置微修正通道**：真机日志定位到 `Pos` 从第一个 epoch 起 100% 分歧而 `phys`/`time`/`Life` 全同；`[AsyncPosAlign]` 实际修正率仅 `0.5629%`、`59%` 的漂移落在 `[0.001, 0.5)` 格被死区丢弃。新增 `MicroAlignFloorCells = 2/PhysicsQuantScale`（默认 `0.0078f`）+ `MicroAlignCooldownTicks = 24` + 无阻尼精确吸附 + 分表冷却；开关 `TABCOOP_PRECISION_ALIGN` 默认开启、`=0` 可回到 v6.3 行为做 A/B。**待真机双机验收**）
> **构建基线**: `v0.1.5` (`0.1.5.0`) `TABCoopMod.csproj` Release 编译 `0 警告 0 错误`；实机注入 `GameBridge 0 missing`，Harmony 补丁 **`Classes OK=64, failed-tolerant=0`**。
> **历史过程记录**: 早期排查过程与会话 1~6 演进流水已归档至 [`docs/archive/`](archive/SESSION_CHANGELOG_S1_TO_S6.md)。

---

## 1. 全门禁与自动化实测验收总表（2026-11-13 实测）

| 门禁 / 用例 | 测试存档与条件 | 实测关键指标与结论 | 状态 |
| :--- | :--- | :--- | :-: |
| **Gate A1（最新回归）** | `1_Backup.zxsav` / 120 ticks 双进程回放 | `[compare] PASS: 120 ticks identical`；`common=2125, moved(>0.01)=0, maxDx=0, maxDy=0` | ✅ 通过 |
| **Gate A2** | `6666.zxsav` / 180 ticks 双进程回放 | `[compare] PASS: 180 ticks identical`；`common=7842, moved(>0.01)=0, maxDx=0, maxDy=0`（加载仅耗时 ~7.87s） | ✅ 通过 |
| **Gate B2** | `6666.zxsav` / 65s Loopback (`latency=0`) | Tick `168484..169440`（956 ticks）连续 **17/17 次 `StateChecksum` 全部 `MATCH`**，`0 DESYNC`，`0 Auto-resync`，`0 ERROR` | ✅ 通过 |
| **Gate B1** | `1_Backup.zxsav` / 65s Loopback (`latency=0`) | Tick `31904..32880`（18 次）全部 `MATCH` → Tick `32940` 原生求解器 `0.002` 格 `Pos` 漂移触发 `Auto-resync`（约 `3.07s` 恢复至 Tick `33058 MATCH`）→ Tick `33058..33480`（9 次）全部 `MATCH`，`0 Client ERROR` | ✅ 通过 |
| **Chaos-1（故障注入）** | `1_Backup.zxsav` / 60s `ChaosTransport` (`latency=120;jitter=30;loss=0.02;disconnect=4000-5000;seed=42`) | `9924` 实体对齐；Tick `31904..32880` 连续 **18 次 `MATCH`** → Tick `32940` 触发一次 `Pos` 微偏 `Auto-resync`（约 `3.12s` 重建对齐 Tick `33075`）→ 继续连续 **6 次 `MATCH`**，`0 Client ERROR` | ✅ 通过 |
| **Chaos-2（故障注入）** | `6666.zxsav` / 60s `ChaosTransport` (`latency=80;jitter=20;loss=0.01;seed=7`) | `33597` 实体对齐；Tick `168484..169380` 连续 **16/16 次 `StateChecksum` 全部 `MATCH`**，`0 DESYNC`，`0 Auto-resync`，`0 ERROR` | ✅ 通过 |
| **Gate C** | `1_Backup.zxsav` / 控制通道 + 截图 | 双端独立相机位置/缩放同步运行；截图目视确认本地白框与队友彩色视野框（Host `DodgerBlue` / Client `SpringGreen`）在小地图正确渲染 | ✅ 通过 |
| **Gate D** | `1_Backup.zxsav` / 暂停微操 + 3s 倒计时 | Client 发起 `request_pause(true)` 同步冻结于 Tick `31972`（显示 `Player 2 Paused Game`）；暂停期间 `move_units` 派发与资源扣减（`gold 355→235`）双端一致；Host 恢复后顶居中 `Resume in 3...` 清晰可见，解冻后单位于双端同步抵达 `(149.49, 132.35)` / `(149.42, 132.01)`，全程 Tick `31904..32100` 连续 5 次 `MATCH` | ✅ 通过 |
| **真人联机缺陷修复** | 双开实机反馈修复与代码闭环 | ① 修复视角卡顿与逻辑阻塞（异步单 tick 流水线 + `RenderVisualInterpolationPatch` 视觉插值 + `SmoothCameraPanPatch`）；② 修复单位无法完成攻击动作（`GameTimeIsolationPatch` / `CSpritePullIsolationPatch` / `CMovableSpriteProgressSyncPatch` 隔离渲染时间污染）；③ 修复重叠放置建筑与无视资源不足（`Patch_ZXCommand_Execute` 预检 + `_recentLocalBuilds` 同面积去重，`CommandExecutor.ExecutePlaceBuilding` 校对 `CanExecuteCommandOnEntity` / `CanPlaceBuildingAtTarget` 并调用 `FlushStartFramePostMethods()`）；④ 修复双端各自选市长领双倍奖励（`MayorModalTrackerPatch` + `MayorSyncPatch` 按 `mayorLevel` / `MayorStates` 幂等去重并自动关闭对端弹窗） | ✅ 已修复 |
| **客机平滑与频次优化** | `1_Backup.zxsav`（120 tick）+ `900survival.zxsav`（`async_align`） | 新增 `ZXBehaviourRunRngScopePatch`（`DynamicMethod TABCoop_GetBehavOwnerEntityId`）按实体 ID + Tick 的无状态 SplitMix64 行为随机流，消除游荡僵尸消耗共享 RNG 引发的级联漂移；`CoopSessionManager` 解耦高频血量/击杀同步（`2s`）与低频软位置对齐（`8s` + 单实体 `180 tick` 冷却去抖），分级阈值 `MinAsyncPosSnapCells = 0.50f` + 移动中单位速度阻尼 + `600ms / τ=180ms` 视觉平滑衰减；`1_Backup` 回放 `120 ticks identical` | ✅ 通过 |
| **并发冲突与建造实测** | `900survival.zxsav`（18,074 实体 / 双端 ControlServer 同时发令） | 同批 3 单位同时反向 `move_units` 按确定性顺序收敛至同一目标（`Host/Client` 均抵达 `(132.89, 114.81)`）；同格 `(120.5, 134.5)` 并发 `ZX.Entities.WallWood` 仅建 **1 座**（`WallWoodDelta Host=1 Client=1`，木材 `20→17` / 金币 `799` 一致，双端同报 `Rejected PlaceBuilding`）；不同格 `(122.5, 132.5)` 与 `(122.5, 134.5)` 并发均建 **2 座**（`WallWoodDelta Host=2 Client=2`，木材 `14→8` / 金币 `769`）；两轮共 28/28 + 22/22 次 `StateChecksum` 全部 `MATCH`，`0 ERROR`，`0 AsyncPosAlign` 强制瞬移 | ✅ 通过 |
| **RTS 等候房间与全流程** | `verify_lobby_flow.ps1` + `1_Backup.zxsav` 双开实测 + `run_replay_verify.ps1` | 主菜单入口/探测本地主机（`1/3`、`InLobby`）/加入初态未准备与房主 `canStart=False` 门控/客机切换就绪解锁开局/复用单人[新建生存游戏]并验证每次滚动动态种子（`-1880374752` vs `859584069`）/取消自动回房间/读档进入同步对局（`3018` 实体对齐）/房主 `ReturnToMainMenu` 双端退回主菜单且保留网络、重置客机为未准备并重开大厅；12 张实机截图无斜切撕裂或换行，双进程回放 `120 ticks identical`、`0 ERROR` | ✅ 通过 |
| **文档 08 梯度一优化** | `1_Backup.zxsav`（120 tick 回放 + `latency=90/0` 双开测试） | 完成本地待建幽灵虚影与 8 项资源预扣累计、0ms 本地单位语音即时确认与回放期 `UnitVoices` 去重、基于 RTT 探测的自适应 `InputDelayTicks (1..4)`；实测 `GameBridge 0 missing`、`Classes OK=57, failed-tolerant=0`、回放 `120 ticks identical`、`latency=90` 升档 `1 -> 4`、`latency=0` 自动降档 `3 -> 2` 且全程 `StateChecksum MATCH`（幽灵框视觉与语音听感待人工桌面确认） | ✅ 已实现 |
| **生存新局平衡与全景对比** | `verify_new_survival_balance.ps1` + 4 场景同种子全景图（`BR`/`DS`/`FA`/`VO`） | 实装 `CoopBalanceManager` + `SurvivalBalancePatch`（含 `MapGenerator` 非 2 的幂次边界 Transpiler `doom=1, raven=1`）+ 大厅 `Co-op Balanced / Vanilla` 切换同步；`Classes OK=58, failed-tolerant=0`；双开校验 Balanced (`NCells=280, extraW=2, 6R+1S+1CC`, Tick 2/120/197 `StateChecksum MATCH`) 与 Vanilla 对照 (`NCells=256, 4R+1S+1CC`) 通过，`0 ERROR`；4 场景全景图目视确认感染村镇回到可建造区中环（`reports/panorama_comparison.html`） | ✅ 通过 |
| **文档 08 梯度二目标 5（物理步末量化截断）** | `1_Backup.zxsav`（120 tick 回放 + 60s `Chaos-1`）+ `900survival.zxsav`（密集群体汇聚/反向行军） | 在 `LockstepClock.UpdatePhysics_Exit` 接入 `QuantizePhysicsBodiesPostStep()`（`TABCOOP_PHYSICS_QUANT=256`，即 `1/256` 格量化写回 `PhysicPosition`/`PositionComputed`/`DXEntity.Position` 与 `PhysicVelocity`/`Velocity`，保留移动体 `Last*` 供视觉插值）；`1_Backup` 回放 `120 ticks identical`、`Chaos-1` 消除原 Tick 32940 位置漂移（Tick `31904..33720` **31/31 `MATCH`、`0 Auto-resync`、`0 ERROR`**，`quant≈0.25ms/p`）；`900survival` 8 单位汇聚+反向行军暂停同 tick 对照 `maxPeerDiff=0.0000`、`minPairDist=0.4617 / 0.1105`、10/10 `MATCH` | ✅ 已实现 |
| **文档 11 控制权差分对齐与视觉平滑优化** | `1_Backup` 120 tick 回放 + `900survival`（`latency=0` & `ChaosTransport` 回归） | ① `CoopSessionManager` 实装 `_unitOwnership` 租约（300t TTL / 90t 闲置宽限）、客机自控保护（`<3.5` 格免拉回）与随 `StateChecksumReport` 捎带的同 tick 轻量反向对齐；② 改由 `SetCamera` + `UpdateVisibleCells` 批量投影全场景（含 `CShadow` 与 Z 深度）、修正 `StartFrame` 累加/扣减固定 `83.33ms` 时序及渲染期视觉 `GameTimeInt` 余数（已回退视觉起步假位移并修复退出主菜单渲染插值 `ArgumentOutOfRangeException`） | ✅ 已实现 |
| **战败/胜利同步与结束界面退出修复** | `1_Backup.zxsav` + `900survival.zxsav` 双开实测（TriggerGameEnd + ConfirmExit） | 新增 `PacketType.LevelEnded (0x23)` 与 `LevelEndSyncPatch`（主机权威广播、抑制客机伪触发并关闭 `HostPauseKeepalive` / `Tick gate waiting` 误报），并在 `LockstepClock.UpdateGameLogic_Prefix` 增加 `Paused`/`!CurrentGameSystem.Enabled`/`IsLevelEnded` 停步保护及 `ReturnToMainMenuPatch` 清理结束画面渐变过渡特效；`900survival` 实测主机触发战败后双端同步弹出结束界面（`endStatus=2, inModal=true, countdownActive=false`），点击 `游戏结束` 退出按钮后双端均顺利退回主菜单重开大厅（`onMainMenu=true, lobbyWindowOpen=true, HostAlive=True, ClientAlive=True, 0 ERROR`） | ✅ 通过 |
| **市长选择对端自动关窗修复** | `900survival.zxsav` 双开双向实测（Round 1 主机先选 / Round 2 客机先选） | 修复本地弹窗暂停（`DXGame.Paused = true`）阻塞 `FlushDueTurnCommands` 及客机等待未来 `TargetSimTick` 未提前关闭弹窗的问题：在 `OnFrameTick` 对本地引擎暂停同样冲刷已就绪回合指令，并在客机收到含 `MayorVoteOrSelect` 的 `TurnInputBroadcast` 时立即调用 `CloseActiveMayorWindow()` 与 `NotifyRemoteMayorCommitted()` 抑制延迟弹窗；双开实测双方向均在 300ms 内自动关闭对端弹窗且 `Paused=false`（`HostMayor=1/2, ClientMayor=1/2`），后续 Tick `4725..4920` 4/4 `StateChecksum MATCH`、`0 ERROR` | ✅ 通过 |
| **Steam P2P 实测 4 项问题修复** | `1_Backup.zxsav` + `900survival.zxsav` 双开实测 + `run_replay_verify.ps1` + `verify_lobby_flow.ps1` | ① `MayorModalTrackerPatch` 在弹窗抽样前按 `(SyncedRandomSeed, mayorLevel)` 重设 `DXLevel.Current.Random` 并在 Postfix 重锚（错帧双开截图同出 `阿什利·达尔` + `米娅·斯特兰德`）；② `ReturnToMainMenuPatch` / `MainMenuOnLoadCompletePatch` / `CoopLobbyWindow` 兜底 `ClearWaitCursor()`；③ 消费 `_pendingInviteLobbyId`、`SteamP2PTransport.Dispose()` 释放 5 个 `Callback<T>`、Host 按 `CSteamID` 幂等去重；④ 新增 `GameSetGameTimePatch` 放行 `DXGame.SetGameTime` 并在退回主菜单归零时钟（战败退出 `cursorLocked=False`，重开生存新局 `GameTime=0.0ms` / `天 000`，Tick `1` 与 `120` `StateChecksum MATCH`，`1_Backup` 回放 `120 ticks identical`，`verify_lobby_flow` 9/9 通过） | ✅ 通过 |
| **版本不一致告警与隐形尸潮修复 (`v0.1.3`)** | `logs/*_3.log` 根因排查 + `1_Backup.zxsav` 120t 回放 + `ChaosTransport` 20s 双开 | ① `INetworkTransport` 新增 `OnVersionMismatch` 事件，`SteamP2PTransport`（Lobby 元数据 `tab_coop_ver` + `HandshakeAccept` 附带版本，兼容旧版无版本尾字段）与 `LocalTcpLoopbackTransport` 双端检测并弹窗/红框告警；② `CoopSessionManager.OnPosDriftSyncChunkReceived` 仅在 `cs.IsDynamic` 扫描且当 `missingOnHost > 8 \|\| missingOnClient > 4` 时禁止批量 `Kill`，避免 ID 偏移时误杀客机新生成的尸潮；③ `VerifyTickChecksum` 收紧 `entityTol = Clamp(EntityCount/1000, 12..30)`、`StructuralReloadStreak = 6`，`PosDriftStatsReport` 接入 `missingOnClient >= 10` 连续 4 次（`30s` 结构冷却）触发 `Auto-resync`；`1_Backup` 120t 回放 `identical`，`Chaos-1` 20s 11/11 `StateChecksum MATCH`、`0 ERROR` | ✅ 通过 |
| **市长赠送单位取出与槽位碰撞修复 (`v0.1.4`)** | `1_Backup.zxsav` 120t 回放 + 双开 Loopback 实机验证（`Mayor_002` + `Mayor_006`） | ① `GameBridge.IsBuildOrBonusCommand` 仅将 `ZX.Entities.Structure` 类型的 `EntityBonusCreation` 归为 `PlaceBuilding` 分支，修正赠送单位被误判为 `(0,0)` 放置而遭吞没的缺陷；② `Patch_ZXCommand_Execute` 与 `ResolveReplayCommand` 为 `EntityBonusCreation` 与 `ExtractFromUnitHolderSlot` 携带真实 `NSlot`/`EntityTypeName` 并在 `AvailableCommands` 中按类型+槽位匹配（防耗尽前移误触发），取出单位后立即 `FlushStartFramePostMethods()`；③ `ExecutePlaceBuilding` 为 `IsBonusBuilding` 校验剩余次数并调用 `ConsumeBonusEntityByType` 与清理放置游标；双开实测 Client/Host 取出 `Ranger(2)`（`16->18`、过期点击未误触发前移的 `ShockingTower`）与放置 `ShockingTower`（`0->1`、次数归零）成功，Tick `31904..34200` 全程 `StateChecksum MATCH`、`0 ERROR` | ✅ 通过 |
| **客机追帧延迟、暂停同步、直连通道与槽位复用修复 (`v0.1.5`)** | `1_Backup.zxsav` 120t 回放 + `verify_lobby_flow.ps1` + `6666.zxsav` `ChaosTransport` 30s 双开 | ① `LockstepClock` 解除追帧时 `canOverlapAsync` 单 tick 硬限制并放宽客机追帧预算至 `28ms`；② `CoopSessionManager` 通过 `RttPong` 捎带客机实时 `SimTick`，主机按落后 tick（`>=16` 减速 `1.35x`、`>=45` 暂停推进，3.5s 失联保护）自动反压并将 `LatencyStats` 接入 `clientLag`/`tickLag`，同步扩大 `_tickPosCache`（32s）与 `_hostTickDigests`（40s），`PauseSyncController` / `HostPauseKeepalive` 增加客机即时横幅反馈；③ `DirectFastPath` 修正 `len < 39` 包头长度、对齐 `BulkWindowChunks=48` 与 64-bit SACK 窗口、增加 3s 数据通道假死自动回退 Steam；④ `SteamP2PTransport` 与 `LocalTcpLoopbackTransport` 按 `2.._maxPlayers` 最小空闲槽位回收复用（杜绝 `P4`/`P5` 隐身）并清理 `OnP2PConnectFail` / `AllyMiniMapOverlayPatch` 残留；实测 `1_Backup` 回放 `120 ticks identical`、`verify_lobby_flow` 9/9 通过、`6666.zxsav` `Chaos-1` 在双开 `15~22 FPS` 下稳定维持 `clientLag=9~10t` / `tickLag=3~5t`，11/11 `StateChecksum MATCH`、`0 ERROR` | ✅ 通过 |
| **亚格位置漂移微修正 (`v6.4`)** | 真机双机联机日志（host `host_FINAL_2033.log` / client `client_FINAL_2234.log`）+ 本机双进程 A/A（`affinity-ab.ps1` / `align-sweep.ps1`） | ① 真机证据：`[StateChecksum]` 从第一拍起 `Pos` 100% DIFF，而同 tick 的 `phys`（物理步数）、`time`（GameTime）、`Life`/`Aggro`/`Proj`/`Resv`/`entities` 逐字符相同；6 段累计 `[AsyncPosAlign]` 修正率仅 **0.5629%**（`11726/2083107`），`Drifted` 分布显示 **59% 的漂移落在 [0.001, 0.5) 格**、被 `MinAsyncPosSnapCells = 0.50f` 死区整段丢弃；② 落地：新增 `MicroAlignFloorCells`（默认 `2/PhysicsQuantScale = 0.0078f`，与 `QuantizeCoord` 的 `1/256` 量化粒度对齐）、`MicroAlignCooldownTicks = 24`、`MicroAlignMaxUnitsPerReport = 2048`，收集侧单列表按 `dist` 分流、应用侧微漂移带**无阻尼精确吸附**（阻尼只走 `55%~75%`，残差会被下一步物理重新放大），微修正与常规修正**分表冷却**（否则被微修过的单位漂到 `>=0.5` 格时会被 `180 tick` 冷却挡住）；③ 开关 `TABCOOP_PRECISION_ALIGN`（默认开，`=0` 回到 v6.3 行为做 A/B）与 `TABCOOP_PRECISION_ALIGN_CELLS`；④ 本机 A/A 对照（`2` 核 vs `20` 核，`align=0` 3 跑 1 次漂 / `align=1` 3 跑 0 次漂）样本量过小、`epoch` 数不足，**只能作为方向性参考，不作为验收结论** | ⚠️ 待真机验收 |

---

## 2. 已确认的工程边界与行为特性

1. **引擎并行循环是失步的主因（v6 已修）**：
   - `DXVision.dll` 的 `DXHelper_Task.For` 按 `minIterationsForConcurrency`/`maxCores` 切块后多线程执行循环体，共享累加器、broadphase 插入、arbiter 候选的访问顺序因此取决于核数与线程调度；两台机器（甚至同机两次运行）得到不同的浮点舍入，逐 tick 累积成位置漂移。
   - 修复方式：Harmony 前缀替换 `DXHelper_Task.For` 的三个重载，在确定性沙箱内退化为串行 `for` 循环；范围覆盖物理 Loop 1（`StartUpdatePhysics`）、Loop 2（`OnNewPositionComputed` 回读）以及逻辑 tick 本身。A/A 对照（同一二进制、同一存档 `PVE2.zxsav`、种子 `20261109`、零输入）此前约一半运行漂移，修复后连续多轮全绿。
   - 代价：17k 实体后期存档 `physFor1 ≈ 6~9ms/t`，帧率约 12~17fps。`TABCOOP_SERIALIZE_LOGIC=0` 可关掉逻辑 tick 的串行化换帧率（物理串行不可关）。
   - 诊断开关：`TABCOOP_PROJ_DUMP=1` 按 epoch 输出 `[ProjDump] Tick=… role=… count=… ids=…`，可直接对比两端抛射物集合。
2. **异步 tick 重叠默认关闭（v6.1）**：`canOverlapAsync` 改为 opt-in（`TABCOOP_ENABLE_ASYNC_TICK=1` 才开）。重叠时逻辑 tick 在 `_TaskLogic` 续体线程上跑引擎 `UpdateGameLogic`，与主线程的渲染/DXScene pass 交错，实体位置被采样的时刻由每台机器的线程调度决定 —— 实时联机里表现为每段开始 60~200 tick 内出现亚像素位置漂移，随后 `MissingOnClient` 攒够就 1~4 分钟重建一次快照。A/A 测试一直靠 harness 设 `TABCOOP_DISABLE_ASYNC_TICK=1` 关着它，所以 18 次连绿并不能证明"开着重叠也是确定的"；发布版此前恰恰跑在默认开启上。同步 tick 与回放路径一致，代价是后期存档吞吐被压在 12~30fps。
   - 诊断开关：每个 epoch 的摘要末尾带 `phys=<DXPhysicWorld.NUpdates>`，两端同 tick 必须相同；不一致时额外打印 `PHYS-UPDATES-MISMATCH host=… client=… delta=…`。`phys=` **不进** `HardHash`/`FullHash`；v6.2 起它随 `StateChecksumReport` 的 wire 末尾追加（append-only，旧构建对端照常解析），MATCH 行不再出现假的 `client=0`。
3. **物理时钟基线不对称是真实双机失步的根因（v6.2 已修）**：
   - 引擎 `DXGame.UpdatePhysics` 的追赶循环是 `while (game.GameTime - DXPhysicWorld.LastGameTimeUpdatePhysics >= 83.333…) { LastGameTimeUpdatePhysics += 83.333…; UpdateDefaultTime(); }`，步长是编译期常量，**物理步数只由这两个 double 决定**。
   - 实测（host pid 29132，Build 2026-11-20 14:08:17，真实双机 Radmin 联机）：逐段统计显示 `dPhys` 在段内恒定、只在 reload 边界 +1（一场联机累积到 18 步）——**client 每 reload 一次就比 host 永久多跑 1 个物理步**。原因是两端各自用本地 `game.GameTime` 落基线（`GameBridge.AlignSimulationClocks`），差 83.33ms；且 `RequestPostLoadCalibration` / `ResetSyncTrackingState` / `OnReturningToMainMenu` 三处把 `LastGameTimeUpdatePhysics` 置 0 后无人重新对齐（上游 v0.1.9 同样如此）。
   - 后果：物理步数不同 → 同一 tick 的实体位置从**第一个 epoch** 就不同（`Pos=` 校验和 841 对里 100% 不同，`Coarse=` 仅 5.5% 相同）→ 逐 tick 累积 → `MissingOnClient` 攒够触发快照重建。这解释了"每段 60~200 tick 就漂"和"同机 A/A 18 连绿但双机必漂"两个看似矛盾的现象。
   - 修复：房主在 `SnapshotBegin`（append-only 字段）里下发自己的物理时钟原点，客机原样采用；`ApplyDeterministicLevelBaseline` 优先用共享原点对齐。新增 `GameBridge.AlignSimulationClocksTo(originMs)`、`LockstepClock.SharedClockOriginMs`、`LockstepClock.ApplySharedClockOrigin(originMs)`。
   - 诊断开关：`[FPFingerprint]`（浮点位模式，两机必须逐字符相同）、`[Baseline]`（基线指纹）、`[BucketDiff]`（首个分歧桶）、`TABCOOP_TRACE_FIRST_TICKS=N`（逐 tick 摘要）。
4. **移植上游 v0.1.9 的确定性修复（v6.2）**：
   - 忽略表现层市民 Colonist（`IsColonistType` + `StateHasher` `FlagIgnore` + `Building.SpawnColonists` 视觉隔离）——他们只在基地里随机走动，不参战不挡建造，却被计入状态校验。
   - `StructureNearEnemiesReplayPatch`：回放建造指令时忽略 `HasNearEnemies`，避免"游荡僵尸距建筑差 0.07 格"导致单边拒建 → 资源分叉 → `econ x3` 重建。
   - 建造预览供电圈隔离（`Suspend/RestoreLocalBuildCursorEnergyPreview`）——本地预览供电圈会让两端 `CanPlaceAtCell` 结论相反。
   - 收紧 5 分钟兜底重载判据（上游 `substantiveDiff` 口径），小幅实体数差不再触发全量重建。
5. **跨 CPU 数学末位差异是同机测不出的最后一类分歧（v6.3 已修）**：
   - 引擎寻路与几何计算用 `Math.Pow(x,2)` / `Math.Pow(x,0.5)`，而 `Math.Pow` 由**平台 CRT 实现**，不保证在每颗 CPU 上都正确舍入：实测 `Math.Pow(x,2)` 与 `x*x` 在 20 万次采样里有 108~421 次 1-ULP 差异。
   - 两台机器的 CPU/CRT 一旦不同 → 路径代价差 1 ULP → 大小比较翻转 → 选到不同路径 → 实体移动差零点几格 → 逐 tick 放大成可见的位置漂移。
   - **按构造就抓不到**：同机两个 peer 跑同一颗 CPU、同一份 CRT，`Math.Pow` 结果逐位相同 ⇒ 同机 A/A 永远全绿（我们连测 18 轮 + v6.3 后 3 轮），只有双机才暴露。这解释了"单机测多少遍都稳、联机必漂"。
   - 修复：新增 `Patches/DeterministicMathPatch.cs`（HarmonyTranspiler）把常量指数的 `Math.Pow` 改写成 CPU 无关的精确形式（`x*x` / `Math.Sqrt` / 连乘），覆盖 4 个方法共 13 个调用点；`Noise::GetOctaveNoise`（指数是运行时整数且实测精确）与 `DXHelper_Trajectory::Lineal`（指数是 `1+accel`）故意不动；非确定模式下退回原始 `Math.Pow`，单机行为不变。
   - 已核实引擎其余超越函数调用都在不进模拟的路径上（`DXMatrix*::Rotate`、`DXObject::RotateAt`、`DXHelper_Random::RandomLocationsInCircle`（无调用点）、`DXTransition::FactorByTime`、`ClipperLib::*`、`DXHelper_Image::*`）；`TheyAreBillions.exe` 侧只有 IEEE 精确的 `Math.Round`/`Floor`/`Ceiling`。
6. **原生 Chipmunk 求解器极微小漂移 + `Auto-resync` 自愈**：
   - 在密集重叠僵尸群下，非托管 `DXPhysicsNative.Space_Step` 偶发 `1e-4` 级别速度微差，累积数十帧后可能产生约 `0.002` 格位置偏离（已通过 `PRE/POST DXPhysicWorld.Step` 验证全部托管输入完全一致）。
   - 已启用的 `Auto-resync` 能在检测到 `StateHash` 不一致后约 **3 秒内**自动重建并恢复一致状态继续锁步，且二次读档已无 `CShadow` NRE。
7. **纯视觉/环境实体不参与对局状态哈希**：
   - 裸 `DXVision.DXEntity`（受击飙血/弹花贴图）与 `Raven`（环境飞鸟）仅作用于视觉表现，在 `StateHasher` 与 `DeterministicReplayVerifier.DumpPositions` 中已明确跳过。
8. **同进程重载 (`inproc`) 仅用于崩溃/资源清理校验**：
   - `PrepareForProgrammaticLoad()` 已彻底消除关卡内二次读档 `CShadow` NRE，但 `inproc` 模式下引擎仍存在跨局静态残留实体总数偏差；确定性比对统一以**双进程模式**为准。
9. **新开局动态随机地图种子（已接入）**：
   - `CoopSessionManager.RollNewGameSeed()` 在正式联机会话（非 `TABCOOP_TEST=1`）下会在建房、点选[新建生存游戏]/[自定义地图]及重返大厅时动态滚动 `CurrentSessionSeed`，使联机新开局每次随机换图，并通过快照/`HostSyncBarrier` 将同一种子精确同步给所有客机；自动化回放与固定种子校验（`TestHarnessConfig.Enabled`）仍保留 `SyncSeed = 20261109`。
10. **未实机覆盖项（需人工桌面/双账号参与，不可假设已完成）**：
   - **真人键鼠操作全链路扩展**：大波尸潮混战下客机位置平滑体感、Shift 连续建造与全部建筑/部队热键组合的长期稳定性；
   - **Steam Lobby P2P 跨机联机**：`SteamP2PTransport`（内嵌 Steamworks.NET v10）需要双 Steam 账号或跨机实测。
11. **亚格位置漂移的修正死区（v6.4 已加微修正通道，待真机验收）**：
    - 真机日志证明：`[StateChecksum]` 的 `Pos`（`0.01` 格量化）从**第一个 epoch** 就 100% 不同，而同 tick 的 `phys`（物理步数）、`time`（`GameTime`）、`Life`/`Aggro`/`Proj`/`Prod`/`Resv`/`entities` 全部逐字符相同 ⇒ 不是时钟、不是步数、不是实体集合，纯粹是**同一步内部算出的坐标不同**。本机用 `affinity-ab.ps1` 把两个进程的 `ProcessorAffinity` 倾斜（`2` 核 vs `20` 核）即可稳定复现同型漂移，证明它是**调度敏感的概率性残留**，与 `DeterministicTaskPatch` 头注释“A/A 大约一半的跑次会漂”一致。
    - 修正通道实效：6 段真实联机累计 `[AsyncPosAlign]` 只应用了 **0.5629%**（`11726/2083107`）；`Drifted` 直方图显示 **59% 的漂移量落在 `[0.001, 0.5)` 格**，被 `MinAsyncPosSnapCells = 0.50f` 整段丢弃，第二层 `GameBridge.GetEntityPositionSnapThreshold` 又给出 `0.50~1.80` 格，两层叠加使得亚格漂移**永远不会被修正**，只能靠 `Auto-resync` 全量重建快照清理。
    - 修复：`CoopSessionManager` 新增 `MicroAlignFloorCells`（默认 `2 / TABCOOP_PHYSICS_QUANT = 0.0078f`，正好是 `LockstepClock.QuantizeCoord` 的量化步长，再小会被 round 掉）、`MicroAlignCooldownTicks = 24`（微修正便宜且频繁，用短冷却而非 `180`）、`MicroAlignMaxUnitsPerReport = 2048`（单次报告上限，防大局卡帧）。收集侧仍是**单列表**，应用侧按 `dist` 分流；微漂移带走**无阻尼精确吸附**（`applyDx = d.Dx`），因为 `dx = hs.X - cs.X` 已是同 tick 真实差，阻尼只走 `55%~75%` 会让残差被下一步物理重新放大。微修正与常规修正**分表冷却**，否则被微修过的单位漂到 `>=0.5` 格时会被 `180 tick` 冷却挡住。微漂移带里**本地玩家自持单位一律不动**（Host 样本已滞后一个 RTT）。
    - 开关：`TABCOOP_PRECISION_ALIGN`（默认开启，`=0` 精确回到 v6.3 行为以便 A/B）与 `TABCOOP_PRECISION_ALIGN_CELLS`（覆盖默认下限）。日志新增 `[AsyncPosAlign] … micro=N` 计数，`PosDriftStatsReport` 回报数也计入微修正。
    - **验收状态**：本机 A/A（`align-sweep.ps1`，`2` 核 vs `20` 核、`200s`/跑）得到 `align=0` 3 跑中 1 跑漂（6 次 `TRANSIENT`，首漂 tick `44220`）、`align=1` 3 跑 0 漂，**但每跑 `StateChecksum` 只有 `0~11` 个 epoch，样本量远不足以定论**；真机双机验收（两台都设 `TABCOOP_TRACE_FIRST_TICKS=2000`，配 `cmptrace.ps1` 看首个分歧 tick 是否后移/消失）**尚未进行**。
12. **`entities` 字段在跨机 baseline 比对里天然不可比（已定位，非缺陷）**：
    - 主机每次加载前走 `GameBridge.PrepareForProgrammaticLoad()`，其中 `if (wasInLevel) game.ClearEntities()` 会清空实体列表；客机加载快照走 `LoadSaveFileAndEnter`（先落盘再加载），**`ClearEntities()` 未被调用** ⇒ 客机 `game.Entities` 保留历史已 `Dispose` 实体（实测 `19522` vs `58882`、`19149` vs `76250`）。
    - 真正参与哈希与物理的集合两端完全相同：`bodies`/`dynamic`/`physPos`/`computed`/`entityPos` 在 7 段 baseline 里逐字符一致（唯一例外是 @`11055` 段 `bodies 12315 vs 12311`，客机少 `4` 个 body，属老存档读档 + 首次连上的边界情况）。⇒ 比对 `entities` 字段会稳定误报，应排除。

---

## 3. 下一步最高优先级任务（Next Steps）

1. **P1 — 真人输入全流程联调（人工桌面测试）**：
   - 在游戏内进入单机拦截模式（`F8`）及双开 Loopback 联机模式，人工逐项验证：框选右键移动/攻击、小地图右键移动、Shift 连续建造与普通放置、兵营训练/取消队列、空格与右上角按钮暂停、`+`/`-` 倍速、市长二选一弹窗、`Alt+左键` 地面+小地图同步打点。
2. **P2 — Steam P2P 跨机联机验收（双 Steam 账号）**：
   - 使用双 Steam 账号/双机测试 `CoopLobbyWindow` 的创建房间、好友邀请、快照传输与 Steam P2P 全流程联机（`ChaosTransport` 故障注入与断连回归已在本地双开全通过）。
3. **P3 — 协同 UX 完善与 Phase 4 预研**：
   - 补全大地图地面队友彩色鼠标光标与建造预览框渲染；细化市长“客机意向高亮 + 房主拍板”交互；视情况启动 Phase 4 独立基地模式。
4. **P0 — `v6.4` 亚格微修正的真机双机验收（未完成，不可假设通过）**：
   - 两台机器都用 `TABCOOP_TRACE_FIRST_TICKS=2000` 启动（主机侧用 `C:\soft\DSH-jobs\001\开启逐tick追踪并启动游戏_主机.bat`），联机 3 分钟以上并保持建造/移动/战斗活动。
   - 用 `C:\soft\DSH-jobs\001\cmptrace.ps1 -HostLog <host> -ClientLog <client>` 配对 `[TickTrace]`，观察首个分歧 `rel` 是否比 v6.3 的 `+150~+524` 明显后移或整体消失；用 `C:\soft\DSH-jobs\001\cmpduallog.ps1` 看 `[BucketDiff]` 条数与 `[StateChecksum]` 的 `SOFT POS DRIFT` 比例。
   - 对照组：同一对机器上设 `TABCOOP_PRECISION_ALIGN=0` 再跑一轮，两边必须用同一份存档、同一会话设置，否则不具可比性。
   - 判定门槛：`SOFT POS DRIFT` 比例与 `[AsyncPosAlign]` 的 `MaxDelta` 峰值显著下降、且 `Exceeded=True` 不再出现，才算通过；仅“这次没漂”不算（漂移本身是概率性的）。
