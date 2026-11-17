# 开发演进日志归档 (Session 1 ~ Session 6 Changelog)

> **说明**: 本文件浓缩保留了项目自 Phase 0 破冰至会话 6 全门禁通过期间的阶段性里程碑（原 `06_HANDOVER_STATUS.md` 过程记录与 `09`/`11` 阶段结果）。当前实时状态与下一步任务请直接查看 [`docs/06_PROJECT_STATUS.md`](../06_PROJECT_STATUS.md)。

---

## 会话 1（2026-11-08 ~ 11-09）：逆向破冰与骨架落地（Git `4bbea80`）
- 编写 `scripts/extract_sdk.ps1` 与 `decompile_type.ps1`，从加密 ManifestResource 提取未混淆 `DXVision.dll` / `Steamworks.NET.dll` 等。
- 攻克免改 EXE 注入：`AppDomainManager` + `AssemblyLoad` 订阅 + `RuntimeHelpers.RunModuleConstructor` + `steam_appid.txt`（防重启秒退）+ `AccessTools` 跨程序集 `internal` 反射调用 + 逐类 `PatchClassProcessor` 容错（移除触发 Eazfuscator 异常块的 `OMiniMap.OnMouseRightDown`）。
- 实机验证 `GameBridge` 27 项绑定 `0 missing`，主菜单成功注入 `CO-OP MULTIPLAYER` 按钮。
- 落地确定性时钟、指令序列化与回放器、Loopback/SteamP2P 传输、`CoopSessionManager` 与大厅弹窗骨架。

## 会话 2（2026-11-09）：自动化确定性回放台落地与 `pos` 收敛
- 落地 `DeterministicReplayVerifier`、`TestHarnessConfig`、`ReplayTimeline`、`ScriptedCommandDriver`、`ChaosTransport` 与 `StateHasher` v2（新增投射物/队列/预留校验）。
- 修复 `GameBridge.CurrentLevelState` 与 `StateHasher` 的 `TypeAccessException`，改为纯 `object` + 反射。
- 修复 `DXGuid` 加载期误耗同步 ID 问题后，`1_Backup.zxsav` 两进程回放实现 `entities/life/proj/prod/resv/经济` 全部一致，分叉精确收敛至仅剩移动单位 `pos`。

## 会话 3（2026-11-09）：指令全反射化与 MCP 自动化测试台
- `CommandExecutor` 与 `ScriptedCommandDriver` 全量 `object` + 反射化，解决 `TypeAccessException`，脚本注入建房/移动/训练零报错。
- 搭建 `ControlServer`（TCP 127.0.0.1:27901 主线程队列）、`ScreenshotService`、`CameraProbe` 与独立 `TABCoopMod.Mcp`（net10.0）stdio 服务端，完成移动/建造/训练/暂停/倍速/市长/开全图/相机与 HTML 分叉报告工具链。
- 在生存中后期档 `6666.zxsav` 上验证，明确生存档是比战役档更严苛的确定性验证基线。

## 会话 4（2026-11-10）：全量反编译审计与 P0~P3 修复落地（Git `9d33782`）
- 交叉审计反编译源码，定位并修复 `CalibrateEntityIdCounterFromLoadedLevel` 异步读档时序错位、物理时间戳清零、`CommandInterceptorPatch` 残留 `HasComponent<CMovable>()`、`PauseSyncController` 路由、`EntityBonusCreation` 市长奖励建筑、`ScenePointFromWorldPoint` 等轴测坐标、`CInvisibleWhenNotWatched` 显形、输入层暂停/倍速（`PauseAndSpeedInputPatch`）与市长四重守卫（`MayorSyncPatch`）。
- 实现 `TurnInputTickGate` + `_pendingTurnCommands` 网络门控，并打通 Host 快照导出广播；识别出 `pos` 二值分叉与 Client 快照卡住两大阻塞转入深度诊断。

## 会话 5（2026-11-10）：打通 Client 快照热加载（Gate B 突破）
- 发现并修复 `.zxcheck` 与存档文件名绑定及 `ChaosTransport` 分片乱序问题，Client 成功载入 `coop_host_snapshot.zxsav` 并与 Host 推进至同一 `SimTick`。
- 实机核对排除若干早期假设（本版本 `DXGame` 无 `WindowActive`、`ZXLevelState` 无私有 `DXRandom`、`DXBehaviour._GlobalRandom` 为 `[ThreadStatic]` 不可直接 Patch getter），为会话 6 最终根因定位扫清障碍。

## 会话 6（2026-11-13）：四大底层根因彻底根治与 Gate A1/A2/B1/B2/C/D 全通过
- **根治二值 `pos` 分叉**：发现 `ZXGame.IsMultiplayer()` 被 x64 RyuJIT 内联绕过补丁，新增 `DeterministicPathfindingPatch` 直接同步拦截 `DXWorldGrid.FindPathConcurrent/WithHighPriority`；过滤纯视觉裸 `DXEntity` 与环境飞鸟 `Raven`；新增 `CSpritePullIsolationPatch` / `CMovableSpriteProgressSyncPatch` / `AudioRngIsolationPatch` / `VisualVfxIsolationPatch` 与 `DXPhysicBody.Empty` 过滤。
- **根治 `6666.zxsav` 加载超时**：仅在 `InPhysicsStep` 串行化 `DXHelper_Task.For`，并将自动读档钩子改挂主线程完成回调 `ZXSystemMainMenu.#=zwK_pUIeRBpbp`，实现 **7.87s** 秒级加载 33,555 实体生存大档。
- **根治关卡内二次读档 `CShadow` NRE**：`GameBridge.PrepareForProgrammaticLoad()` 补全禁用旧系统并清理实体，重新启用并实测验证 `Auto-resync`（~3.07s 自愈）。
- **完善协同与显示兼容**：`OnFrameTick` 支持 `PausedBySession` 下微操实时执行；`Patch_DXRender_VirtualDisplayFallback` 兼容无物理显示器；修复 `AllyMiniMapOverlayPatch` 透明底图边框与 `PauseSyncController` 顶居中 `DXTextObject` 3 秒倒计时。
- **最终洁净构建（`Classes OK=40, failed-tolerant=0`）全门禁通过**：Gate A1、Gate A2、Gate B1、Gate B2、Gate C、Gate D 全部实机验收通过。
