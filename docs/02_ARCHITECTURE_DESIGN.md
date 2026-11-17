# 02 — 核心技术架构设计 (Technical Architecture Design)

> **状态**: 已实机验证定型（Gate A1/A2/B1/B2/C/D 全通过）

---

## 1. 架构选型总览：Deterministic Lockstep + Native Save-Stream Snapshot

《亿万僵尸》（TAB）在生存中后期同屏存在 **10,000 ~ 33,000+ 个实体**（如 `6666.zxsav` 含 33,555 实体）：
- **纯状态同步不可行**：全量实体状态广播带宽高达数 MB/s，远超 Steam P2P 转发能力。
- **纯指令帧同步痛点**：无法低成本支持开局随机地图同步与极偶发算力漂移自愈。

因此本项目采用 **“确定性指令帧同步为主 + 原生存档流快照秒传为辅”** 的混合架构：

```text
+-----------------------------------------------------------------------------------+
|                              TABCoopMod 运行时架构                                 |
+-----------------------------------------------------------------------------------+
|                                                                                   |
|  [无损注入层 Bootstrap]                                                            |
|   TheyAreBillions.exe.config -> ModAppDomainManager (AssemblyLoad)                |
|   -> <Module>..cctor -> GameBridge.InitializeAndValidate() -> Harmony 逐类容错加载  |
|   -> Patch_DXRender_VirtualDisplayFallback (无物理显示器 Direct3DEx 兼容)           |
|                                                                                   |
|  +-------------------------------+  +------------------------------------------+  |
|  | 确定性沙盒层 (Determinism)     |  | 指令拦截与无状态回放层 (Command Sync)     |  |
|  | - DXGame.IsIADeterministic=true| | - CommandInterceptorPatch (5 处主入口)    |  |
|  | - DeterministicPathfindingPatch| | - PauseAndSpeedInputPatch (输入层暂停/倍速)|  |
|  | - DeterministicTaskPatch       | | - MayorSyncPatch (4 重守卫按 MayorId 应用) |  |
|  |   (仅 InPhysicsStep 串行)      | | - CommandExecutor (全 object+反射，不碰    |  |
|  | - DXGuid 双轨分流与载入后校准    | |   CSelectable，模拟 Shift 状态)          |  |
|  | - DXRandom 多维定种与线程隔离    | +--------------------+---------------------+  |
|  +---------------+---------------+                        |                        |
|                  |                                        |                        |
|                  v                                        v                        |
|  +-----------------------------------------------------------------------------+  |
|  | 帧同步编排层 (CoopSessionManager & TurnInputTickGate & PauseSyncController)  |  |
|  | - 30Hz 固定步长 (33.333336ms/tick)，TurnInputTickGate 按 TargetSimTick 门控  |  |
|  | - PausedBySession 真正冻结 SimTick，OnFrameTick 持续刷新暂停期间微操指令      |  |
|  | - 浅栈导出/加载 .zxsav+.zxcheck 快照流 + 60 tick StateHasher v2 对账与自愈    |  |
|  +---------------------------------------+-------------------------------------+  |
|                                          |                                        |
|                                          v                                        |
|  +-----------------------------------------------------------------------------+  |
|  | 双模传输抽象层 (INetworkTransport)                                           |  |
|  | - SteamP2PTransport: SteamMatchmaking Lobby + SteamNetworking P2P           |  |
|  | - LocalTcpLoopbackTransport: 127.0.0.1:27099 (支持 ChaosTransport 故障注入)  |  |
|  +-----------------------------------------------------------------------------+  |
+-----------------------------------------------------------------------------------+
```

---

## 2. 无损注入与显示兼容（Bootstrap & Render Compat）

### 2.1 `.NET AppDomainManager` 引导（`ModAppDomainManager.cs`）
由于 `TheyAreBillions.exe` 内置 Eazfuscator 完整性与 `StackTrace.GetFrame(3)` 校验，Mod 保持主程序磁盘字节 100% 不变，仅通过 `TheyAreBillions.exe.config` 注入 `ModAppDomainManager`：
1. 在 `InitializeNewDomain` 初始化日志与全局异常监听，订阅 `AppDomain.CurrentDomain.AssemblyLoad`。
2. 当 `TheyAreBillions` 加载完成时，先执行 `RuntimeHelpers.RunModuleConstructor(loaded.ManifestModule.ModuleHandle)` 注册内置资源解密器（`DXVision` / `Steamworks.NET` / `DXPlatform_Desktop`）。
3. 调用 `ModEntry.Initialize()` → `GameBridge.InitializeAndValidate()`（启动自检必须 `0 missing`）→ 逐类 `PatchClassProcessor.Patch()` 包 `try/catch` 应用全部 Harmony 补丁（实机 `Classes OK=56, failed-tolerant=0`）。

### 2.2 虚拟显示器 `Direct3DEx` 回退（`Patch_DXRender_VirtualDisplayFallback`）
在无物理显示器的虚拟显示环境（如远程虚拟屏适配器报错 `D3DERR_NOTAVAILABLE`）下，对纯 C# 封装类 `DXPlatform_Desktop.DXRender_SlimDX` 的 9 个方法应用 IL Transpiler：当 `Direct3D.GetDeviceCaps` 失败时自动回退至 `SlimDX.Direct3D9.Direct3DEx`，并将 `Pool.Managed` 纹理替换为 `Pool.Default | Usage.Dynamic`（避开直接补丁 C++/CLI 混合程序集 `SlimDX.dll` 导致的 `InvalidProgramException`）。

---

## 3. 确定性沙盒层（Determinism Sandbox）

核心代码位于 `src/TABCoopMod/Determinism/`（`LockstepClock.cs`、`DeterministicTaskPatch.cs`、`DXRandomCtorPatch.cs`、`TickGate.cs`、`StateHasher.cs`）：

### 3.1 固定步长时钟与 `TurnInputTickGate`
- **开启确定性主循环**：`DXGame.IsIADeterministic = true`，阻止 `LoadPhysics`/`LoadPhysicsOld` 启动异步计时线程。
- **固定步长推进**：在 `UpdateGameLogic_Prefix` 中检查 `LockstepClock.TickGate.IsTickReady(nextTick)`。
  - 若 `PausedBySession == true`：通过 `HoldClocks(__instance)` 冻结 `GameTime` 与 `LastGameTimeIAUpdate`，**真正冻结 `SimTick`**。
  - 若运行中且 Gate 就绪：按固定步长 `FixedStepMs = 33.333333333333336` 推进，并在关卡加载完成及基线对齐时通过 `GameBridge.AlignSimulationClocks()` 将 `LastGameTimeIAUpdate` 与 `DXPhysicWorld.LastGameTimeUpdatePhysics`、`TimeBodyPosition`、`TimeLastBodyPosition` 同步对齐到 `GameTime`，避免相差相位或清零导致单帧物理狂步。
- **窗口焦点与帧模数隔离**：联机与测试模式下强制 `DXGame.Focused/Visible = true`（保证后台窗口正常驱动 `DXScene` 动画）；`FrameSceneModuleDeterminismPatch` 将 `DXGame.IsFrameSceneModule(n)` 改为按 `IAIterationInt % n` 判定，切断本地渲染帧率对逻辑分支的影响。

### 3.2 同步寻路接管（`DeterministicPathfindingPatch`）
- **根因**：原版 `ZXGame.#=zB3Vhdin5PerfrubjO4AblAA=()`（`IsMultiplayer`）仅 6~8 字节 IL，会被 x64 RyuJIT 内联进 `CMovable.#=zUPt0E_k=`，导致 `ZXGameMultiplayerModePatch` 被绕过、寻路落入后台多线程 `FindPathConcurrent` 产生 A/B 二值轨迹分叉。
- **架构**：直接拦截引擎层 `DXVision.DXWorldGrid.FindPathConcurrent` 与 `FindPathConcurrentWithHighPriority`（位于 `DXVision.dll`，不会被内联），在当前逻辑线程同步调用 `DXWorldGrid.FindPath`（保留单位体型 `cellSize` 与空路径字段 `#=ziMavkfK9ZJji` 清空逻辑，规避原版多人分支的体型硬编码与递归栈溢出 Bug）。

### 3.3 仅在物理步进串行化并行任务（`DeterministicTaskPatch`）
- 反编译全部 `DXHelper_Task.For` 调用点确认：读档期（`DXLevel.LoadTable`）与场景渲染裁剪（`DXScene.UpdateObjects`）是只读/独立任务，**只有 `DXPhysicWorld.UpdateDefaultTime()` 内的两次循环影响模拟状态**。
- 因此 `DeterministicTaskPatch` 仅在 `LockstepClock.IsDeterministicModeActive && LockstepClock.InPhysicsStep` 为 `true` 时将 `DXHelper_Task.For` 转为单线程顺序执行，同时在 `SortEntitiesById` 中剔除 `null` 与 `DXPhysicBody.Handle == IntPtr.Zero` 的空句柄并按 ID 升序排列。大存档 `6666.zxsav`（33,555 实体）得以保持 **~7.8 秒**原生级加载速度。

### 3.4 `DXGuid` 双轨分流与 `DXRandom` 多维定种
1. **`DXGuid.New()` 双轨分流**：
   - 仅当处于确定性逻辑/命令上下文（`InDeterministicContext || LogicTickActive` 且非加载期）时消耗同步 ID 计数器（`_syncedEntityIdCounter`，支持校准与 `SnapshotBegin` 同步）；
   - 加载期及本地 UI/渲染特效一律分配高位 ID（`0x8000000000000000UL | localCounter`），`StateHasher` 自动忽略高位 ID。
2. **`DXRandom` 全域隔离与按实体行为随机流**：
   - `DXRandomCtorPatch` 拆分同步模拟线程与非模拟/渲染线程的种子计数器，防止主线程动画插队污染逻辑序列。
   - `ZXBehaviourRunRngScopePatch`（`DynamicMethod TABCoop_GetBehavOwnerEntityId`）将 `ZXBehaviour.Run()` 内的行为随机调用拦截到基于 `(SyncedRandomSeed, SimTick, Entity.ID, drawIdx)` 的无状态 SplitMix64 序列，消除单个游荡僵尸消耗共享 RNG 造成的级联漂移。
   - `LockstepClock` 对 `DXRandom.Global` 按上下文分流（`synced` / `aux` / `render`）；同步重置 `DXLevel.Random`、`SysColonists` 游标与列表、`BHFarmer` 农田选格随机，并隔离音频（`AudioRngIsolationPatch`）、纯视觉特效（`VisualVfxIsolationPatch`）、帧时间冻结（`GameTimeIsolationPatch`）、视口精灵拉取（`CSpritePullIsolationPatch` / `CMovableSpriteProgressSyncPatch`）的污染；同时通过 `RenderVisualInterpolationPatch`（位置纠偏残差按 `VisualSnapExpireMs = 600.0`、`VisualSnapTauMs = 180.0` 指数衰减）与 `SmoothCameraPanPatch` 保障渲染帧间移动与平移平滑。

### 3.5 `StateHasher` v2（顺序无关校验与非对局实体过滤）
- FNV-1a 64 位校验，覆盖资源、预留、实体坐标（量化 `Math.Round(v * 100)`）、生命值、投射物与生产队列。
- 跳过不参与模拟交互的裸 `DXVision.DXEntity`（受击飙血/弹花等纯视觉特效，依赖视野可见性）与 `Raven` 环境飞鸟。

---

## 4. 指令拦截与无状态回放系统（Command Interception & Replay）

### 4.1 拦截点（`Patches/`）
- **`CommandInterceptorPatch.cs`**：拦截 `Build`/`EntityBonusCreation` 建造放置、`ZXSystemGameLevel` 群体移动（`#=zQp2M56bpPd9jmiNkVA==` / `#=zgn6bDkSd9MGS3$WvAQ==`）、通用命令 `ZXCommand.#=zfmgzkuw=`、取消当前命令（`#=zWGiN6nrTv8Dp`）与取消队列槽位（`#=z8g7GC7xLrr8j`）。对建造拦截增加 `CanExecuteCommandOnEntity`、`CanPlaceBuildingAtTarget` 与 `_recentLocalBuilds` 短窗面积去重，防止连续误触或资源不足透支排队；所有守卫均排除回放期、生产队列自动推进（`#=zrz2EI1A=`）、确定性逻辑帧内部 AI 调用以及非玩家阵营，且严禁对内部类型调用 `HasComponent<T>()`。
- **`PauseAndSpeedInputPatch.cs`**：从 `ZXSystemGameLevel.OnKeyDown` / `OnKeyUp` 拦截空格暂停与 `+`/`-` 倍速键，交由 `PauseSyncController` 与 `CommandInterceptor.Dispatch` 处理。
- **`MayorSyncPatch.cs`**：Hook `ZXMayor.#=zPTw8CXGE0RqF` 与 `ShowMayorSelection`（`MayorModalTrackerPatch`），带四重守卫与 `mayorLevel` / `MayorStates` 幂等去重；客机收到包含 `MayorVoteOrSelect` 的 `TurnInputBroadcast` 时立即调用 `CloseActiveMayorWindow()` 与 `NotifyRemoteMayorCommitted()` 关闭对端弹窗并抑制未弹出延迟窗口，配合 `OnFrameTick` 在本地模态暂停期主动冲刷已就绪指令，确保单侧点选后对端即时关窗、每级市长奖励只生效一次。
- **`LevelEndSyncPatch.cs`**：Hook 房主 `TriggerGameEnd` / `ShowCampaignEnd`（并在 `OnFrameTick` 兜底轮询 `IsLevelEnded()`），权威广播 `PacketType.LevelEnded` 使客机同步进入 `游戏结束` 结算且抑制 `HostPauseKeepalive` / `Tick gate waiting` 误报；`LockstepClock` 在 `Paused` / `!CurrentGameSystem.Enabled` / `IsLevelEnded()` 时停步且 `ReturnToMainMenuPatch` 清理结束画面渐变特效 `#=z40N_lqqB8daJ`，防止结束界面退出主线程卡死（`AppHangB1`）。

### 4.2 无状态回放与暂停微操（`NetCommand.cs` & `CoopSessionManager.cs`）
- `CommandExecutor` 全量使用 `object` + `GameBridge` 反射，**绝不读取或修改 `CSelectable`**；执行期间临时模拟指令携带的 `IsShiftQueued` 按键状态并带有 `_executionDepth` 递归深度保护。`ExecutePlaceBuilding` 在执行前复用 `CanExecuteCommandOnEntity` + `CanPlaceBuildingAtTarget` 校验资源与格子占用，并在落盘建造后立即调用 `GameBridge.FlushStartFramePostMethods()` 刷新网格占位，保证同 tick 内双方同格并发建造时后一条确定性拒建不重复扣费。
- **暂停态实时微操**：当 `LockstepClock.PausedBySession == true` 时，`UpdateGameLogic` 不触发，由 `CoopSessionManager.OnFrameTick()` 在 `RunInDeterministicContext` 保护下对冻结的当前 `pausedTick` 主动调用 `FlushDueTurnCommands(pausedTick)`，从而在暂停下即时执行建造/拉兵/训练/解暂指令并立即渲染上屏。

---

## 5. 内存存档流同步与 `Auto-resync`

1. **同名 `.zxcheck` 校验与乱序分片重组**：
   - 引擎 `ReadSaveFile` 校验存档时将 `.zxcheck` 与 `.zxsav` 文件名绑定。Host 与 Client 统一读写 **`coop_host_snapshot.zxsav` / `coop_host_snapshot.zxcheck`**。
   - 客机使用 `Dictionary<int, byte[]>` 按分片索引缓冲重组，容忍传输抖动重排。
2. **浅调用栈导出/加载（消除 `0xc00000fd` 栈溢出）**：
   - `OnLevelLoadComplete`（`ZXSystemGameLevel.#=zwK_pUIeRBpbp`）或收到分片后只设置 `_pendingHostSnapshotExport` / `_pendingClientSnapshotLoadPath` 标志，统一延迟到下一帧 `CoopSessionManager.OnFrameTick()` 顶层浅栈执行 `WriteSaveFile` / `LoadSaveFileAndEnter`。
3. **无焦点淡入推进与 `CShadow` NRE 修复**：
   - 客机加载期间强制 `Focused = true` 并每帧调用 `GameBridge.KickLoadingFade()` 推进原生黑屏淡入动画，使 `loadAction` 顺利执行。
   - 调用读档前执行 `GameBridge.PrepareForProgrammaticLoad()`：显式将旧 `CurrentGameSystem.Enabled = false`、置 `Paused = true` 并调用 `ClearEntities()`，补齐原版 `LoadGameAndEnter` 第 229 行 `== null` 空判写反导致的二次读档 `CShadow` `NullReferenceException`。
4. **周期校验、`async_align` 与 `Auto-resync`**：
   - 每 60 逻辑帧比对 `StateHash`；Host 与 Client 采用对称热重载保证基线对齐。
   - 默认 `async_align` 模式下，通过 `TrySendPosDriftSync` / `OnPosDriftSyncChunkReceived` 将高频血量/击杀修正（`2s`）与低频软位置对齐（`8s`、`MinAsyncPosSnapCells = 0.50f`、单实体 `180 tick` 冷却去抖，遇 `maxDelta >= 3.5f || rmsDelta >= 1.2f` 放行应急对齐）解耦；仅当出现不可自愈的经济/结构或大面积残余分叉时触发快照重同步，实测约 **3 秒内**完成恢复。

---

## 6. 协同 UX 渲染（`UI/`）

- **联机等候大厅与菜单生命周期（`CoopLobbyWindow.cs` & `MainMenuPatch.cs`）**：在主菜单注入 `CO-OP MULTIPLAYER` 按钮、`MainMenuOnLoadCompletePatch` 与 `ReturnToMainMenuPatch`，基于原版 `ZXWindow(BigWindow)` 与 `ZXButtonSteam` 构建入口探测视图与 3 槽位等候房间（切换文本/颜色前先清理 `DXTextObject.Image` 避免 `Direct3DEx` 下 `ReLoadInGPU()` 纹理斜切），并在客机全员就绪后通过反射复用单人模式的读档/新建生存/自定义地图弹窗，取消选图或从局内返回主菜单时保留网络连接自动重开大厅。
- **小地图队友视野框（`AllyOverlayRenderer.cs` → `AllyMiniMapOverlayPatch`）**：Hook `OMiniMap.OnRender` Postfix，为队友创建带 `DXImage.FromColor(Color.Transparent)`、`Z = 1001`、`PixelAligned = true` 与 `2px` 边框的 `DXObject`，通过 `DXHelper_Rectangle.ChangeCoordinates(ally.CameraWorldArea, PlayableArea, miniMap.InnerArea)` 实时更新。
- **暂停提示与 3 秒恢复倒计时（`PauseSyncController.cs`）**：暂停横幅使用 `durationMs = 0` 确保暂停帧立即 100% 不透明显示；恢复倒计时在 `DXIsometricGridObject.Current` 上挂载顶居中固定像素 `DXTextObject`（`Resume in 3/2/1...`），解冻时立即移除。
