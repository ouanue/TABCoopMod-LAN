# 05 — 分阶段开发路线图与门禁标准 (Development Roadmap & WBS)

> **状态**: Phase 0 & Phase 1 已完结；Phase 2 & Phase 3 核心门禁已全部通过，进入真人交互联调与 Steam P2P 跨机验收阶段。

---

## 阶段总览 (Milestone Overview)

| 阶段 | 名称 | 核心交付目标 | 当前状态 |
| :---: | :--- | :--- | :---: |
| **Phase 0** | **工具链就位与注入破冰** | 提取内嵌 SDK，搭建 `TABCoopMod.csproj`，实现 `AppDomainManager` 无损注入、`GameBridge` 自检（0 missing）、逐类容错 Harmony 加载与主菜单按钮。 | ✅ 已完成 (100%) |
| **Phase 1** | **确定性沙盒与指令回放** | 确定性时钟、同步寻路、物理单线程、`DXGuid` 分流、`DXRandom` 定种隔离、7 类指令反射回放、`StateHasher` v2。通过 `1_Backup`（120 tick）与 `6666`（180 tick）双进程零分叉回放门禁（Gate A1/A2）。 | ✅ 已完成 (100%) |
| **Phase 2** | **双模网络层与内存存档流秒传** | `LocalTcpLoopbackTransport`、`SteamP2PTransport`、`ChaosTransport`、`CoopLobbyWindow`；`coop_host_snapshot` 32KB 分片同步与客机无焦点热加载。通过 Loopback 双端长时同步与 Auto-resync 门禁（Gate B1/B2）。 | 🔄 95%（Loopback 已通过，待 Steam P2P 跨机实测） |
| **Phase 3** | **执政官模式协同 UX 与全量联调** | `TurnInputTickGate` 门控、暂停冻结 `SimTick` + 暂停微操、3 秒恢复倒计时、市长按 ID 同步、小地图队友视野框与 Ping。已通过控制通道/截图验收（Gate C/D），待真人键鼠操作全链路联调与 Chaos 高压压测。 | 🔄 85% |
| **Phase 4** | **独立基地模式拓展（第二阶段）** | 多指挥中心生成、Mod 层虚拟阵营资源账本隔离、单位所有权锁定与多路尸潮分流。 | ⏳ 远期规划 |

---

## 各阶段核心交付物（对应当前实际源码）

### Phase 0：工具链与注入地基 ✅
- 工具脚本：[`scripts/extract_sdk.ps1`](../scripts/extract_sdk.ps1)、[`scripts/decompile_type.ps1`](../scripts/decompile_type.ps1)、[`scripts/install_mod.ps1`](../scripts/install_mod.ps1)、[`scripts/uninstall_mod.ps1`](../scripts/uninstall_mod.ps1)
- 注入与核心：[`Bootstrap/ModAppDomainManager.cs`](../src/TABCoopMod/Bootstrap/ModAppDomainManager.cs)、[`Core/ObfuscatedSymbols.cs`](../src/TABCoopMod/Core/ObfuscatedSymbols.cs)、[`Core/GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)、[`Core/ModLogger.cs`](../src/TABCoopMod/Core/ModLogger.cs)
- 入口 UI：[`Patches/MainMenuPatch.cs`](../src/TABCoopMod/Patches/MainMenuPatch.cs)

### Phase 1：确定性沙盒与指令系统 ✅
- 确定性内核：[`Determinism/LockstepClock.cs`](../src/TABCoopMod/Determinism/LockstepClock.cs)、[`Determinism/DeterministicTaskPatch.cs`](../src/TABCoopMod/Determinism/DeterministicTaskPatch.cs)、[`Determinism/DXRandomCtorPatch.cs`](../src/TABCoopMod/Determinism/DXRandomCtorPatch.cs)、[`Determinism/TickGate.cs`](../src/TABCoopMod/Determinism/TickGate.cs)、[`Determinism/StateHasher.cs`](../src/TABCoopMod/Determinism/StateHasher.cs)
- 指令与回放：[`Commands/NetCommand.cs`](../src/TABCoopMod/Commands/NetCommand.cs)、[`Patches/CommandInterceptorPatch.cs`](../src/TABCoopMod/Patches/CommandInterceptorPatch.cs)
- 测试与验证台：[`Testing/DeterministicReplayVerifier.cs`](../src/TABCoopMod/Testing/DeterministicReplayVerifier.cs)、[`Testing/ReplayTimeline.cs`](../src/TABCoopMod/Testing/ReplayTimeline.cs)、[`Testing/ScriptedCommandDriver.cs`](../src/TABCoopMod/Testing/ScriptedCommandDriver.cs)、[`Testing/TestingLifecyclePatches.cs`](../src/TABCoopMod/Testing/TestingLifecyclePatches.cs)、[`Testing/ControlServer.cs`](../src/TABCoopMod/Testing/ControlServer.cs)、[`Testing/ScreenshotService.cs`](../src/TABCoopMod/Testing/ScreenshotService.cs)、[`Testing/CameraProbe.cs`](../src/TABCoopMod/Testing/CameraProbe.cs)

### Phase 2 & Phase 3：网络同步、快照流与协同 UX 🔄
- 网络传输：[`Networking/INetworkTransport.cs`](../src/TABCoopMod/Networking/INetworkTransport.cs)、[`Networking/SteamP2PTransport.cs`](../src/TABCoopMod/Networking/SteamP2PTransport.cs)、[`Networking/ChaosTransport.cs`](../src/TABCoopMod/Networking/ChaosTransport.cs)
- 会话与同步：[`Sync/CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)、[`Sync/PauseSyncController.cs`](../src/TABCoopMod/Sync/PauseSyncController.cs)、[`Sync/CoopBalanceManager.cs`](../src/TABCoopMod/Sync/CoopBalanceManager.cs)
- 交互拦截与渲染：[`Patches/PauseAndSpeedInputPatch.cs`](../src/TABCoopMod/Patches/PauseAndSpeedInputPatch.cs)、[`Patches/MayorSyncPatch.cs`](../src/TABCoopMod/Patches/MayorSyncPatch.cs)、[`Patches/SurvivalBalancePatch.cs`](../src/TABCoopMod/Patches/SurvivalBalancePatch.cs)、[`UI/CoopLobbyWindow.cs`](../src/TABCoopMod/UI/CoopLobbyWindow.cs)、[`UI/AllyOverlayRenderer.cs`](../src/TABCoopMod/UI/AllyOverlayRenderer.cs)

#### Phase 2/3 剩余待验收项
1. **真人键鼠操作全链路验证持续收尾**：已实机修复视角移动卡顿、攻击动作中断、重叠建造透支、市长双倍奖励与客机高频修正抖动问题；待继续覆盖大波尸潮交战平滑体感与其余热键边界。
2. **Chaos 高延迟/丢包/断连压测**：在 `ChaosTransport` 注入丢包与断连窗口下跑通 60s+ 双端锁步与自动恢复。
3. **Steam P2P 双账号跨机实测**：验证 `SteamP2PTransport` Lobby 创建、好友邀请与 P2P 通道稳定传输（注意同一 Steam 账号同机双开可能受 Steam 客户端限制，建议双账号或跨机验证）。
4. **多人 UX 细节与并发防护**：已完成同帧同位置建造冲突扣费保护（`ExecutePlaceBuilding` 校验 + `_recentLocalBuilds` 去重）、并发冲突指令收敛与市长单次去重；后续补充客机地面队友光标/建筑预览框渲染、市长“客机推荐高亮 + 房主拍板”交互。

### Phase 4：独立基地模式拓展（远期规划）⏳
- 开局对称多基地生成、Mod 层虚拟阵营资源池与顶栏 UI 隔离、单位所有权锁与尸潮分流。
