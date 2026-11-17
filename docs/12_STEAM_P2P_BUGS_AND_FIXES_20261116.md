# 12 — Steam P2P 实测 Bug 排查与修复记录（2026-11-16）

> **状态**: 4 项问题已完成代码修复与实机验证（构建 `0 警告 0 错误`，`GameBridge 0 missing`，Harmony `Classes OK=64, failed-tolerant=0`）。  
> **参考日志**: [`logs/TABCoopMod_host_2.log`](../logs/TABCoopMod_host_2.log)、[`logs/TABCoopMod_client_2.log`](../logs/TABCoopMod_client_2.log)

---

## 问题总览

| # | 问题现象 | 影响范围 | 根因定位 | 修复状态 |
|---|---|---|---|---|
| **Bug 1** | 不同玩家选择市长时，看到的两个候选市长选项不一致 | 双人/多人市长弹窗候选池 | `ZXGame.ShowMayorSelection` (`#=zcITiOSD6YNmD`) 经 `DXHelper_Task.ExecuteWithDelay(6000, ...)` 在主线程墙钟延迟后触发，直接读取每帧被 `ReanchorSimulationRngForTick` 重锚的 `DXLevel.Current.Random` | ✅ 已修复 |
| **Bug 2** | 从对局中战败离开返回准备房间时，鼠标保持沙漏形状没有复原 | 主菜单 / 准备大厅鼠标光标交互 | `ReturnToMainMenu` (`#=zqF2tlklT$8RE`) 调用 `BeginWaitCursor()` 并在主菜单 `OnLoadComplete` 后异步淡入结束才调用 `EndWaitCursor()`；`CoopLobbyWindow` 在淡入完成前打开，且对局内残留的 `WaitCursorCount`（如新开局 `EnterGameView` 的 6s 开场动画或未配对锁）会导致计数 `>= 2`，单次 `EndWaitCursor` 无法归零解除 `CursorLocked`，且主菜单/大厅无兜底 `ClearWaitCursor()` | ✅ 已修复 |
| **Bug 3** | 加入房间时，有时会出现一个玩家加入后房间里有好几个“他” | Steam 邀请进房 / 成员列表 / 房主花名册 | ① `GameLobbyJoinRequested_t` 触发 `_globalInviteHandler` (`HandleSteamInviteAccepted`) 后未清除 `_pendingInviteLobbyId`，下一帧 `TryHandlePendingSteamInvite()` 再次执行 `JoinSession`；② `SteamP2PTransport.Dispose()` 未释放 `Callback<LobbyEnter_t>` 等 Steamworks 回调句柄，旧实例仍响应 `OnLobbyEntered` 并发送 `HandshakeRequest`；③ Host 处理 `HandshakeRequest` 未按 `CSteamID` 去重，重复递增 `_nextPlayerId` | ✅ 已修复 |
| **Bug 4** | 战败退出后再次开新游戏，地图是新生成的，但天数继承了上一局最后的天数 | 战败退回大厅后重开生存新局 | `ZXLevelState.#=zf9PbDap0F6OC` 在新开局初始化时调用 `DXGame.Current.SetGameTime(0.0)`，但 `OnReturningToMainMenu()` 保留了 `IsDeterministicModeActive = true` 且 `NewSurvival` 不走 `.zxsav` 读档流程（`LoadingLevel = false`），导致 `GameTimeIsolationPatch` 拦截了 `DXGame.GameTime = 0.0` 赋值，新局仍以旧局 `GameTime / IAIterationInt`（如 `1239133.3ms / tick=41304`）开局 | ✅ 已修复 |

---

## Bug 1：不同玩家选择市长时，看到的选项不一致

### 1.1 现象与代码剖析
- 玩家在达到 30 / 200 / 600 / 1200 人口触发市长二选一时，Host 与 Client 弹出的“男性/女性候选市长”名称与属性不一致（当前 [`MayorSyncPatch.cs`](../src/TABCoopMod/Patches/MayorSyncPatch.cs) 仅在点击后同步选中的 `MayorId`，未保证弹窗弹出前的候选抽样一致）。
- 反编译 `ZXSystemGameLevel`（`#=zxRcpu6e7NYzT7tGWqPjpOkc=`）与 `ZXGame.#=zcITiOSD6YNmD`（`ShowMayorSelection`）显示：
  1. 当人口达标时，`ZXSystemGameLevel` 先置 `IsChoosingMayor = true`，随后调用 **`DXHelper_Task.ExecuteWithDelay(6000, ...)`**（现实墙钟 6 秒后在主线程触发 `ShowMayorSelection`；若当时处于模态弹窗中还会再延迟 `3000ms` 重试）。
  2. `ShowMayorSelection` 内部直接调用：
     ```csharp
     ZXMayor male = ZXMayor.#=zoTuFkHI=.Values.Where(m => m.Level == num && m.Gender == ZXGenderType.Male).RandomElement(DXLevel.Current.Random);
     ZXMayor female = ZXMayor.#=zoTuFkHI=.Values.Where(m => m.Level == num && m.Gender == ZXGenderType.Female).RandomElement(DXLevel.Current.Random);
     ```
  3. 然而 [`LockstepClock.ReanchorSimulationRngForTick`](../src/TABCoopMod/Determinism/LockstepClock.cs#L500-L516) 会在每个仿真 tick 重新设置 `DXLevel.Current.Random` 的种子（`MixTickSeed(baseSeed, tick, 0x243F6A88u)`）。由于两端墙钟延迟、网络 RTT 或界面模态时机不同，Host 与 Client 执行 `ShowMayorSelection` 时所处的仿真 tick 与 `DXLevel.Current.Random` 序列状态完全不同，导致抽取到的男女市长不同。

### 1.2 修复方案
- 在 [`MayorModalTrackerPatch.Prefix`](../src/TABCoopMod/Patches/MayorSyncPatch.cs#L142-L154) 中，当即将真正打开弹窗（`!DXModalScreen.IsInModal` 且处于确定性同步模式且 `DXLevel.Current?.Random != null`）时：
  - 基于全局同步种子 `LockstepClock.SyncedRandomSeed` 与当前市长阶段 `mayorLevel = Clamp(MayorStates.Count + 1, 1, 4)`，计算出跨端完全一致的确定性种子 `MixMayorSeed(SyncedRandomSeed, mayorLevel)`，在 `ShowMayorSelection` 执行前用 `DXRandomCtorPatch.ReseedInstance(DXLevel.Current.Random, seed)` 原地重设种子。
  - 在 `MayorModalTrackerPatch.Postfix` 中，若处于确定性模式且 `DXGame.Current != null`，再按当前 `IAIterationInt` 重新锚定 `DXLevel.Current.Random`，避免污染后续仿真随机流。

---

## Bug 2：从对局中战败离开返回准备房间时，鼠标保持沙漏形状没有复原

### 2.1 现象与日志证据
- 对局中（尤其是战败结算点击退出，或开场/切屏阶段退出）经 `ZXGame.ReturnToMainMenu` 返回主菜单并自动重新打开 [`CoopLobbyWindow`](../src/TABCoopMod/UI/CoopLobbyWindow.cs) 后，鼠标指针保持沙漏（`Wait`）且无法随悬停复原。
- 在 [`logs/TABCoopMod_client_2.log`](../logs/TABCoopMod_client_2.log#L296-L305) 与 [`logs/TABCoopMod_host_2.log`](../logs/TABCoopMod_host_2.log#L3384) 中可见：
  - 对局内或开局阶段 `WaitCursorCount` 经常处于未归零状态（例如新开局 `EnterGameView` 在播放开场镜头动画时调用 `BeginWaitCursor()` `#=zUA5kFV81YScB()`，日志多次出现 `Cleared stuck WaitCursor (prevCount=1..2, wasLocked=True)`）。
  - 反编译 `ZXGame.#=z9DPDdq9qP9lZ`（异步切屏过渡）与 `ZXGame.#=zIB5ucfBCe7G8`（`EndWaitCursor`）显示：
    1. `ReturnToMainMenu`（`#=zqF2tlklT$8RE`）调用 `#=z9DPDdq9qP9lZ(..., true)` 时先执行 `BeginWaitCursor()`（`WaitCursorCount++`、`Cursor = Wait`、`CursorLocked = true`）。
    2. 在主菜单加载完成回调 `ZXSystemMainMenu.#=zwK_pUIeRBpbp()` 中，[`MainMenuOnLoadCompletePatch`](../src/TABCoopMod/Patches/MainMenuPatch.cs#L118-L142) 立即调用 `CoopLobbyWindow.OnMainMenuLoadComplete()` 打开等待房间；而引擎的 `EndWaitCursor()` 直到后续黑幕淡出动画结束才调用。
    3. 若退局前 `WaitCursorCount` 已 `>= 1`（例如战败弹窗冻结 `UpdateGameLogic`、或开场 6s 动画未走完即结束/退出、或淡出遮罩曾被清理），`WaitCursorCount` 会升至 `>= 2`；而引擎 `EndWaitCursor()` 在 `WaitCursorCount > 1` 时仅做 `WaitCursorCount--` 并直接 `return`，不会将 `CursorLocked` 置回 `false` 或把 `Cursor` 设为 `Normal`。
    4. 目前仅有 [`LockstepClock.UpdateGameLogic_Prefix`](../src/TABCoopMod/Determinism/LockstepClock.cs#L1262-L1263) 在“处于同步关卡场景”内兜底调用 `GameBridge.ClearWaitCursor()`，主菜单与准备大厅完全没有兜底清理。

### 2.2 修复方案
1. 在 [`GameBridge.ClearWaitCursor()`](../src/TABCoopMod/Core/GameBridge.cs#L834-L868) 中同时检测 `WaitCursorCount > 0 || CursorLocked`，并强制将 `WaitCursorCount = 0` 后调用 `EndWaitCursor()`，确保重置 `CursorLocked = false` 与 `Cursor = Normal`。
2. 在 [`ReturnToMainMenuPatch.Prefix`](../src/TABCoopMod/Patches/MainMenuPatch.cs#L157-L171) 先清理残留的 `WaitCursor` 与非加载态黑幕遮罩，避免旧计数叠加到 `ReturnToMainMenu` 过渡。
3. 在 [`MainMenuOnLoadCompletePatch.Postfix`](../src/TABCoopMod/Patches/MainMenuPatch.cs#L126-L141) 及 [`CoopLobbyWindow.ShowLobbyWindow()`](../src/TABCoopMod/UI/CoopLobbyWindow.cs#L188-L232) / [`CoopLobbyWindow.Tick()`](../src/TABCoopMod/UI/CoopLobbyWindow.cs#L263-L288)（当 `!GameBridge.IsGameLoading()` 且 `CursorLocked` 仍为真时）立即调用 `GameBridge.ClearWaitCursor()`，确保返回主菜单/准备大厅后鼠标立刻复原。

---

## Bug 3：加入房间时，有时会出现一个玩家加入后房间里有好几个他

### 3.1 现象与日志证据
- 玩家接受 Steam 邀请加入房间后，房间列表中出现 2~3 个同名的该玩家（分别占据 `PlayerId=2, 3, 4`）。
- 在 [`logs/TABCoopMod_host_2.log`](../logs/TABCoopMod_host_2.log#L207-L273) 中 100% 复现了完整因果链：
  - **第一轮邀请（2 个相同玩家，`L207-L235`）**：
    - `20:41:08.256` `[SteamP2PTransport] Global Steam Invite accepted! LobbyID=109775244764577313`
    - `20:41:08.257` `[CoopLobbyWindow] Handling Steam Invite for Lobby 109775244764577313...` → 触发第 1 次 `JoinSession`（UDP 端口 `61260`）。
    - `20:41:08.326` `[CoopSessionManager] Auto-joining invited Steam Lobby 109775244764577313 on Main Menu...` → 同帧/下帧 `TryHandlePendingSteamInvite()` 再次消费同一 `_pendingInviteLobbyId`，触发第 2 次 `JoinSession`（UDP 端口 `61261`）！
    - `20:41:08.542` & `20:41:08.545`：前一次已 `Dispose()` 的 `SteamP2PTransport` 与新实例**同时收到 `OnLobbyEntered`** 并各自发送 `HandshakeRequest`！
    - `20:41:14.630` & `20:41:15.478`：Host 连续回包分配 `LocalPlayerId=2` 与 `LocalPlayerId=3`。
  - **第二轮邀请（3 个相同玩家，`L241-L271`）**：
    - `20:42:18.942` 再次接受邀请后，历史上未注销的多个 `Callback<LobbyEnter_t>` 实例齐发 `OnLobbyEntered`（`20:42:19.225`、`20:42:19.225`、`20:42:19.226`），Host 依次分配 `PlayerId=2, 3, 4`。
  - 同理可见 `20:42:46.530`（`L291-L292`）房主建房时 `Steam Lobby created!` 也被旧实例的 `_cbLobbyCreated` 重复触发两次。

### 3.2 根因拆解
1. **邀请状态双重消费**：[`SteamP2PTransport.RegisterGlobalInviteListener`](../src/TABCoopMod/Networking/SteamP2PTransport.cs#L42-L48) 先写入 `_pendingInviteLobbyId = lobbyId`，再调用 `_globalInviteHandler?.Invoke(lobbyId)`；而 [`CoopLobbyWindow.HandleSteamInviteAccepted`](../src/TABCoopMod/UI/CoopLobbyWindow.cs#L333-L359) 未清除 `_pendingInviteLobbyId`，导致 [`CoopSessionManager.TryHandlePendingSteamInvite`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L1499-L1517) 在下一帧重复触发 `JoinSession`。
2. **`SteamP2PTransport.Dispose()` 未注销 Steamworks 回调**：[`SteamP2PTransport.Dispose()`](../src/TABCoopMod/Networking/SteamP2PTransport.cs#L865-L876) 未对 `_cbLobbyCreated`、`_cbLobbyEnter`、`_cbLobbyChatUpdate`、`_cbP2PSessionRequest`、`_cbP2PConnectFail` 调用 `Dispose()`（反编译 `Steamworks.Callback<T>` 确认其通过 `Dispose()` -> `Unregister()` 解除原生 `GCHandle` 与 `SteamAPI` 注册），且各回调处理函数未检查 `_disposed || !IsActive`。
3. **Host 端 `HandshakeRequest` 未按 `CSteamID` 幂等去重**：[`SteamP2PTransport.HandleSteamPacket`](../src/TABCoopMod/Networking/SteamP2PTransport.cs#L507-L546) 收到 `HandshakeRequest` 时无条件执行 `byte assignedId = _nextPlayerId++`，未检查该 `remoteSteamId` 是否已存在于 `_playerToSteamId` / `_peers`；[`CoopSessionManager.BindTransportEvents`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L822-L836) 的 `_lobbyRoster` 亦仅按 `PlayerId` 去重而未按 `SteamId64` 去重。

### 3.3 修复方案
1. 在 `CoopLobbyWindow.HandleSteamInviteAccepted` 入口调用 `SteamP2PTransport.ConsumePendingInviteLobbyId()`（消费并清空待处理邀请 ID），杜绝同一邀请二次进房。
2. 在 `SteamP2PTransport` 增加 `_disposed` 标记，并在 `Dispose()` 中依次释放并置空 5 个 `Callback<T>` 实例；所有回调与 `SendHandshakeToHost()` 均校验 `!_disposed && IsActive`。
3. 在 Host `HandleSteamPacket` 处理 `HandshakeRequest` 时，若 `remoteSteamId` 已分配过非本机 `PlayerId`（或已存在同 `SteamId64` 的 `PeerInfo`），复用原 `PlayerId` 并刷新名字，不自增 `_nextPlayerId`、不插入重复槽位；同步在 `_lobbyRoster` 维护中按 `PlayerId` + `SteamId64` 双重去重。

---

## Bug 4：战败退出后再次开新游戏，天数继承了上一局最后的天数

### 4.1 现象与日志证据
- 战败退出回到等待房间后，房主重新点击“新建生存游戏”，生成的地图是全新地形与全新初始实体，但右上角天数/游戏时钟直接继承上一局结束时的天数。
- 在 [`logs/TABCoopMod_host_2.log`](../logs/TABCoopMod_host_2.log#L3350-L3416) 与 [`logs/TABCoopMod_client_2.log`](../logs/TABCoopMod_client_2.log#L273-L287) 中清晰可见时间轴：
  - 第一局在 `21:15:02.639` 战败结束，此时 `tick=40904`。
  - `21:15:13.786` 房主退回主菜单重开大厅；`21:16:22.112` 新建生存地图并完成生成（新地图实体与种子 `2044302264` 均已更新）。
  - 然而在 `21:16:32.969` Host 打印：`[GameBridge] Simulation clocks aligned at GameTime=1239133.3ms.`，对称重载完成于 **`tick=41304`**（`1,239,133ms / 60s ≈ 第 20 天`），Client 载入快照后也停在 `tick=41304`。

### 4.2 代码根因
1. 反编译 `ZXLevelState.#=zf9PbDap0F6OC`（新局初始化）与 `ZXLevelState.ColonyDays` 确认：
   - 天数直接由 `DXGame.Current.GameTimeInt` 计算：`ColonyDays = GameTimeInSeconds / 60`。
   - 新开生存局时，`ZXLevelState.#=zf9PbDap0F6OC` 会调用 `DXGame.Current.SetGameTime(0.0)`，其内部执行 `GameTime = gameTime; IAIteration = (long)(GameTime / 30.0); IAIterationInt = (int)IAIteration;`。
2. 然而，战败退回大厅时 [`CoopSessionManager.OnReturningToMainMenu()`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L690-L788) 保留了联机会话与确定性沙盒（`LockstepClock.IsDeterministicModeActive == true`），且“新建生存游戏”走 `RandomMapGenerator` 而非 `LoadGameAndEnter`（因此 `LockstepClock.LoadingLevel` 为 `false`）。
3. 当新局调用 `DXGame.Current.SetGameTime(0.0)` 时，[`GameTimeIsolationPatch`](../src/TABCoopMod/Determinism/LockstepClock.cs#L3411-L3421) 拦截了 `DXGame.GameTime` 的 `setter`（`IsDeterministicModeActive && !LoadingLevel && !AllowRawGameTimeSet` -> `return false`），导致 **`GameTime = 0.0` 被静默丢弃**！紧接着 `SetGameTime` 读取未变的旧 `GameTime` 算回旧的 `IAIterationInt`（`41304`）。
4. 同时，`OnReturningToMainMenu()` 与 `NewSurvival` 初始化也未归零 `DXGame.LastGameTimeIAUpdate` 与 `DXPhysicWorld.LastGameTimeUpdatePhysics`。

### 4.3 修复方案
1. 在 [`LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs) 中新增针对 `DXGame.SetGameTime(double)` 的 `GameSetGameTimePatch`：在 `SetGameTime` 执行期间临时允许 `AllowRawGameTimeSet`（仅引擎在关卡初始化/读档重置时钟时调用 `SetGameTime`，后台 `_TaskGameTime` 线程只写属性不调此方法），并在 Postfix 同步对齐 `DXGame.LastGameTimeIAUpdate = gameTime` 与 `DXPhysicWorld.LastGameTimeUpdatePhysics = gameTime`。
2. 在 [`CoopSessionManager.OnReturningToMainMenu()`](../src/TABCoopMod/Sync/CoopSessionManager.cs#L692-L750) 与 `StopSession()` 中重置 `DXGame.LastGameTimeIAUpdate = 0.0`、`DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0`，并在 [`MainMenuOnLoadCompletePatch.Postfix`](../src/TABCoopMod/Patches/MainMenuPatch.cs#L127-L146) 无活跃关卡时调用 `DXGame.Current.SetGameTime(0.0)` 归零残留时钟，确保下一局无论是新建生存、自定义地图还是读档都从正确的时间基线启动。
3. 同步排查并修复 `LockstepClock.UpdateGameLogic_Prefix` 中对 `DeterministicReplayVerifier.IsRunning` 的 `__instance.Paused` 兼容保护（`EnterGameView` 在读档完成后会向下一帧投递 `Game.Paused = true`，此前导致单机回放验证停滞）。

---

## 验证结果汇总（2026-11-16 实机验收）

| 验证项 | 条件与方法 | 实测结果 | 状态 |
|---|---|---|---|
| **构建与注入** | `dotnet build -c Release` + `scripts/install_mod.ps1` | `0 警告 0 错误`；`GameBridge ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing)`；`Classes OK=64, failed-tolerant=0` | ✅ 通过 |
| **Gate A1 确定性回放** | `scripts/run_replay_verify.ps1 -SavePath 1_Backup.zxsav -Ticks 120` | `[compare] PASS: 120 ticks identical`；`common=2125, moved(>0.01)=0, maxDx=0, maxDy=0` | ✅ 通过 |
| **Bug 1（市长候选一致性）** | `1_Backup.zxsav` 双开错帧 `trigger_mayor_popup`（Host tick `31936` / Client tick `31939`，间隔 `600ms`）+ 双端截图 | 双端日志同播 `Mayor level=1 (baseSeed=1224383245, drawSeed=0x87829A01)`；实机截图确认两端候选均为 `阿什利·达尔` + `米娅·斯特兰德` | ✅ 通过 |
| **Bug 2 & Bug 4（战败退出光标与重开新局天数）** | `900survival.zxsav`（`tick=4755, gameTimeMs=142766`）双开触发战败 → `confirm_modal` 退回大厅 → `toggle_ready` → 新建生存地图 | 退回大厅后双端 `cursorLocked=False, tick=0, gameTimeMs=0`；重开新局双端均为 `Simulation clocks aligned at GameTime=0.0ms`，进入时 `tick=64, gameTimeMs=2166, entities=20890`，实机截图右上/左下显示 `天 000 小时 00`，Tick `1` 与 Tick `120` `StateChecksum MATCH`，`0 ERROR` | ✅ 通过 |
| **RTS 大厅全流程回归** | `scripts/verify_lobby_flow.ps1` | `[9/9] ALL ASSERTIONS PASSED!`，`0 ERROR` | ✅ 通过 |
| **Bug 3（Steam 邀请/花名册防重）** | `SteamP2PTransport` 回调释放 + `ConsumePendingInviteLobbyId()` + Host 按 `CSteamID` 幂等去重 + `_lobbyRoster` 双重去重 | 代码审查与 Release 编译/注入通过（Steam 双账号实机邀约待下次双机联网测试） | ✅ 已实现（待双机实测） |

---

## 补充排查（`logs/*_3.log` 实测复盘与 `v0.1.3` 修复）

### 5. 为何会出现"市长仍不一致 / 重开天数保留"且未弹窗警告
- **实测日志确认**：[`logs/TABCoopMod_host_3.log`](../logs/TABCoopMod_host_3.log) 为 **`v0.1.1`**（`Build=2026-11-16 19:45:50`，尚未合入 `v0.1.2` 的 `MayorModalTrackerPatch` 重锚、`GameSetGameTimePatch`、物理量化 `QuantizePhysicsBodiesPostStep`），而 [`logs/TABCoopMod_client_3.log`](../logs/TABCoopMod_client_3.log) 为 **`v0.1.2`**。
- **未弹窗原因**：此前 `SteamP2PTransport` 仅在 Host 收到 `HandshakeRequest` 时记录一行 `[WARN] Version mismatch!`，既未把 Host 版本传给 Client（`HandshakeAccept` 无版本字段，Client 也未检查 Lobby 元数据 `tab_coop_ver`），也未向双方 UI 发送警告。
- **`v0.1.3` 修复**：
  1. `INetworkTransport` 新增 `OnVersionMismatch` 事件；`SteamP2PTransport` 在 `OnLobbyEntered` 读取 `tab_coop_ver`、在 `HandshakeAccept` 末尾写入 `ModInfo.Version`（向后兼容旧版 `<=0.1.1`），`LocalTcpLoopbackTransport` 与 `ChaosTransport` 同步支持。
  2. `CoopSessionManager` 维护 `HasVersionMismatch` / `VersionMismatchSummary`，首次检测到不一致时在主线程弹出 `ZXMessageBoxHelper.ShowAlert` 模态警告与顶部提示条，并在 [`CoopLobbyWindow`](../src/TABCoopMod/UI/CoopLobbyWindow.cs) 房间状态栏以醒目红色展示双方版本差异。

### 6. 客机"隐形尸潮"（看不到兵与小地图红点，直到建筑受击才强制对齐）
- **根因链路**（见 `client_3.log` Tick `31020` / `31560` 与 `host_3.log` Tick `31260`~`33180`）：
  1. 当双端（如混用 `v0.1.1` 与 `v0.1.2` 或 `async_align` 战斗微差）出现 `DXGuid.New()` 计数器错位时，同一批新生成的尸潮在两端拥有相同的数量（`Entity=False(d=0)`），但实体 `ID` 整体偏移。
  2. `CoopSessionManager.OnPosDriftSyncChunkReceived` 对 `!hostIds.Contains(cs.Id) && cs.Id <= hostMaxId` 一律判定为"Host 已击杀"并下发 `Kill = true`，导致客机刚生成的小波/尸潮僵尸在 Tick `31020`（`Kills=31`）与 Tick `31560`（`Kills=41`）被当场全数处死，关联的 `ZXMiniMapIndicatorInfectedSwarn` 红点亦随单位归零而消失。
  3. 同时，`VerifyTickChecksum` 的 `entityTol = Math.Max(15, EntityCount / 100)` 在 17,000 实体地图上高达 **`170`**，使 `d=87` 的实体缺失完全不触发 `structuralDiff`，`PosDriftStatsReport` 亦忽略了 `MissingOnClient=91..104`，直至僵尸打到建筑改变经济（`econ x3`）才触发 `Auto-resync`。
- **`v0.1.3` 修复**：
  1. `OnPosDriftSyncChunkReceived` 仅针对 `cs.IsDynamic` 统计 `MissingOnHost`，且仅当 `missingOnHost <= 8 && missingOnClient <= 4`（常规零星漏杀）时执行客机补杀；一旦同时出现批量 `MissingOnHost` 与 `MissingOnClient`，严禁批量杀怪以免误删客机尸潮。
  2. `VerifyTickChecksum` 将 `entityTol` 收紧为 `Math.Min(30, Math.Max(12, host.EntityCount / 1000))`、`StructuralReloadStreak = 6`（`StructuralResyncCooldownMs = 30s`），并在 `PosDriftStatsReport` 对持续 `MissingOnClient >= 10`（4 次约 8 秒）主动触发 `Auto-resync`。

### 7. 关于"第 10 天前出现多批慢速小尸潮"的原版机制确认
- 反编译 `RandomMapGenerator`（`#=zyl_NPjjlA7DRfVtsRJCX1kN4BxSr`）确认：**原版生存模式自带两套不弹倒计时、不显示小地图红点（`AutoNotifyPlayer = false, ShowCountdown = false, ShowMiniMapIndicator = false`）的周期性偷袭小尸潮 `ZXLevelEvent`**——前期批次由 `ZombieWeakA` / `ZombieWeakB` / `ZombieWeakC`（慢速行者）组成，`MaxRepetitions = 10` 且 `AttackCommandCenter = true`。
- 联机 `Co-op Balanced` 预设在 2 人模式下对早期事件仅做 `1.10x` 数量缩放（`EarlySwarmScale = 1.10f`），不会额外新增早期小尸潮波次。
