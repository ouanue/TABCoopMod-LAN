# 疑难问题根因复盘档案 (Root Cause Postmortems — Resolved)

> **归档说明**: 本文档整理并浓缩了原 `07_TECHNICAL_HARD_PROBLEMS.md`、`09_AUDIT_AND_WORKPLAN.md`、`10_OPEN_QUESTIONS.md` 与 `11_DEEP_DIAGNOSIS_AND_NEXT_DESIGN.md` 中的全部重大疑难问题与反编译根因结论。**所有下列问题均已解决或明确工程边界**，保留于此供历史追溯与防回退。

---

## 1. 确定性回放 A/B 二值 `pos` 分叉：RyuJIT 内联 + 后台异步寻路竞态

- **现象**：早期 `1_Backup.zxsav` 双进程回放前 36~68 tick 完全一致，随后出现稳定且仅有两种离散轨迹的 `pos` 分叉（A/B 双吸引子）；生存档 `6666.zxsav` 进一步暴露实体/生命/投射物分叉。
- **反编译根因**：
  1. `ZXGame.#=zB3Vhdin5PerfrubjO4AblAA=()`（`IsMultiplayer`）仅 6~8 字节 IL，在 x64 RyuJIT 编译 `CMovable.#=zUPt0E_k=` 时被**直接内联为静态字段读取**，绕过了 HarmonyX 的 `ZXGameMultiplayerModePatch`。
  2. `CMovable` 于是继续走单机后台线程 `DXWorldGrid.FindPathConcurrent`（由 `"PathFinding Thread 0/1"` 消费），而 `CMovable.#=z5GPRaBw=` 每 2 逻辑帧轮询一次完成队列——亚毫秒级 OS 线程调度决定了路径在第 $N$ 轮还是第 $N+1$ 轮被消费，形成高度离散的 A/B 稳态。
  3. 同时揭露原版 `CMovable` 多人分支自身存在两处 Bug：硬编码 `cellSize = 1`（忽略 2×2 大单位）及 `FindPath == null` 时无条件递归导致栈溢出。
- **根治方案**：在 `LockstepClock.cs` 新增 `DeterministicPathfindingPatch`，直接拦截引擎层 `DXWorldGrid.FindPathConcurrent` / `FindPathConcurrentWithHighPriority`（位于 `DXVision.dll`，不会被内联），在当前逻辑线程同步调用 `DXWorldGrid.FindPath` 并立即回调 `onPathFound`。

---

## 2. 非对局视觉实体干扰：`DXEntity` 飙血特效与 `Raven` 环境飞鸟

- **现象**：排除寻路竞态后，3018 个真实对局实体位置 100% 吻合，但回放中期短暂出现 2 个裸 `DXVision.DXEntity` 坐标微偏，以及环境飞鸟 `Raven` 引发差异。
- **反编译根因**：`#=zg10wm6mM9hhx` 生成受击飙血、弹花等裸 `DXEntity`（仅 35 tick 寿命）时参考 `IsPixelAreaVisible` / `VisibleCells`；真实单位/建筑/僵尸/投射物均继承 `ZX.Entities.ZXEntity`。
- **根治方案**：在 `StateHasher.ComputeDigest` 与 `DeterministicReplayVerifier.DumpPositions` 中跳过 `entity.GetType() == typeof(DXEntity)` 与 `Raven`，并辅以 `CSpritePullIsolationPatch` / `CMovableSpriteProgressSyncPatch` / `AudioRngIsolationPatch` / `VisualVfxIsolationPatch` 隔离渲染与音频副作用。

---

## 3. `DXRandom` 线程污染、泛型 `[ThreadStatic]` 与 `DXGuid` 校准时序

- **早期错漏 1（低 ID 假位移）**：`CalibrateEntityIdCounterFromLoadedLevel` 曾放在异步 `LoadGameAndEnter` 返回后立即执行（读档未完成），导致计数器停留在 `100000` 起跳并与新生成投射物/僵尸发生跨跑 ID 复用。→ **修法**：读档完成回调 `ZXSystemGameLevel.#=zwK_pUIeRBpbp`（`LevelLoadCompletePatch`）与基线帧校准，加载期 `DXGuid.New` 走高位 UI ID。
- **早期错漏 2（物理时间戳归零）**：`ResetStaticsForRun` 曾将 `DXPhysicWorld.LastGameTimeUpdatePhysics` 置 `0.0`，导致中后期存档（`GameTime` 数百万 ms）单帧狂步上万次物理。→ **修法**：新增 `GameBridge.AlignSimulationClocks()` 统一对齐到 `DXGame.Current.GameTime`。
- **早期错漏 3（RNG 串流）**：`DXRandomCtorPatch` 早期未隔离渲染线程与模拟线程；`DXBehaviour<BData>._GlobalRandom` 实为 `[ThreadStatic]` 且泛型 getter 无法被 HarmonyX 补丁。→ **修法**：拆分模拟与非模拟种子计数器；`EntityUpdateBehaviourRandomPatch` 在逻辑线程内按“线程 + 载入代次”重置行为随机；同步定种 `DXLevel.Random`、`SysColonists` 与 `BHFarmer`。

---

## 4. `6666.zxsav` 加载 >210s 超时 & `DXHelper_Task.For` 串行化误区

- **现象**：大存档 `6666.zxsav`（33,555 实体）最初在确定性测试与 Control 模式下均超时未进图。
- **根因（两层）**：
  1. **并行范围过宽**：将 `DXScene.UpdateObjects()`（3.3 万实体近 10 万渲染节点）与读档期 `DXLevel.LoadTable` 强行单线程化导致性能雪崩；反编译所有 `DXHelper_Task.For` 调用点确认**只有 `DXPhysicWorld.UpdateDefaultTime()` 影响模拟**。→ `DeterministicTaskPatch` 改为仅在 `InPhysicsStep` 时串行化，同时 `SortEntitiesById` 过滤 `DXPhysicBody.Empty` 空句柄。
  2. **主菜单启动竞态**：`Patch_MainMenu_OnLoad_TestHook` 过早挂在后台线程 `ZXSystemMainMenu.OnLoad`（`T8`），主线程随后完成 `ZXSystemMainMenu.#=zwK_pUIeRBpbp()` 并调用 `Scene.Clear()`，直接清掉了 `LoadSaveFileAndEnter` 刚挂入的场景淡出黑屏对象，导致读档静默被吞！→ 钩子改挂主线程完成回调 `#=zwK_pUIeRBpbp` 且读档期间持续 `KickLoadingFade` 后，`6666.zxsav` 实测 **7.87 秒**完整进图。

---

## 5. Client 快照加载静默失败：`.zxcheck` 路径绑定 + 后台窗口冻结动画

- **现象**：Client 收到快照后调用 `LoadSaveFileAndEnter` 5ms 即返回且永久停在主菜单。
- **根因**：
  1. 引擎 `IsSaveLoadable`（`ZXGame.#=zGQN$8GM48urIpvI5bA==`）校验的 `.zxcheck` 与存档**文件名绑定**，Client 曾写为 `coop_client_sync.zxsav` 导致静默失败；统一为 `coop_host_snapshot.zxsav/.zxcheck` 后通过。
  2. `ChaosTransport` 抖动会乱序到达 `SnapshotChunk`，旧实现按到达顺序写 `MemoryStream` 损坏存档；改为 `Dictionary<int, byte[]>` 按索引缓冲重组。
  3. 双开时后台窗口丢失焦点使 `DXScene` 暂停淡出动画推进；通过强制 `Focused/Visible = true` 并调用 `KickLoadingFade` 解决。
  4. **栈溢出 `0xc00000fd`**：`LevelLoadComplete` Postfix 深栈中同步压缩序列化大图 + 网络回调重入耗尽主线程 1MB 栈；改为置标志延迟至 `CoopSessionManager.OnFrameTick()` 浅栈顶层导出/加载，并加 `_executionDepth` 守卫。

---

## 6. 关卡内二次读档 `CShadow` NRE：原版空判写反 Bug

- **根因**：反编译 `ZXGame.#=z9IpK6P4YlW8GIY7olg==` 第 229 行发现原版存在笔误：`if (CurrentGameSystem == null) CurrentGameSystem.Enabled = false;`（与 `ReturnToMainMenu` 相反）。关卡内直接重读时旧系统未禁用，过渡帧 `SetCamera -> UpdateSceneObjectParallel -> CShadow` 访问已销毁的 `SceneObject` 崩溃。
- **根治**：`GameBridge.PrepareForProgrammaticLoad()` 在调用读档前显式将 `CurrentGameSystem.Enabled = false`、置 `Paused = true` 并调用 `ClearEntities()`，实测 `inproc` 重载与 `Auto-resync` 0 NRE。

---

## 7. 原生 Chipmunk `Space_Step` 极微漂移与 `Auto-resync` 验证

- **实测证据**：在 `1_Backup.zxsav` Tick 32709 前后对 `PRE/POST DXPhysicWorld.Step(1/12)` 逐拍全字段对比，确认全部托管输入（`PhysicPosition`、`PhysicVelocity`、`PhysicForce`、`PhysicMass`、`Bodies` 顺序与数量）逐位一致；密集重叠僵尸经非托管 C++ `DXPhysicsNative.Space_Step` 步进后偶发 `1e-4` 级别速度微差，累积至 Tick 32940 产生 `0.002` 格纯位置偏移。
- **工程对策**：启用 `Auto-resync`（周期 60-tick `StateHash` 对账），实测触发后约 `3.07s` 完成全图快照重同步并恢复连续 `MATCH`。

---

## 8. 渲染/坐标/输入与虚拟显示器系列坑点

- **等轴测相机坐标**：`entity.Position` 为 WorldPoint，`DXScene.CameraArea` 为旋转 45° 的 ScenePoint；需 `DXWorldGrid.ScenePointFromWorldPoint` + `DXScene.SetCameraCenter` 并关闭 `#=zfFXkD0aj1o9gF76ANQ==` 开局拉近动画。
- **开全图僵尸显形**：`CInvisibleWhenNotWatched` 读取 `ZXSystemMapLayers.#=z69ntQguW1qCr` 前台视野数组（无视 `ShowFullMap`）；清零视野/探索数组并置 `SceneObject.Opacity = 1f` 即可第 1 帧显形。
- **小地图视野框与暂停倒计时**：`OMiniMap.OnRender` 视野框必须赋 `DXImage.FromColor(Color.Transparent)` 否则无绘制图元；`DXGame.Paused = true` 冻结原生横幅淡入动画，故暂停提示用 `durationMs=0`，恢复倒计时改用独立顶居中 `DXTextObject`。
- **虚拟显示器兼容**：无物理显示器报错 `D3DERR_NOTAVAILABLE` 时，在纯 C# `DXRender_SlimDX` 上通过 Transpiler 切换 `Direct3DEx` + `Pool.Default | Usage.Dynamic`（避免直接 Patch 混合程序集 `SlimDX.dll` 触发 `InvalidProgramException`）。
