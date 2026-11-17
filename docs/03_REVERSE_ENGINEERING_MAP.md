# 03 — 逆向工程符号映射字典 (Reverse Engineering Symbol Map)

> **目标主程序**: `TheyAreBillions.exe` (Version `1.0.14.29`, 64-bit, `4,804,608` bytes)
> **目标引擎库**: `DXVision.dll` (Version `1.0.0.0`, 从主程序 ManifestResource 解密提取，`1,233,408` bytes)
> **代码对应文件**: [`src/TABCoopMod/Core/ObfuscatedSymbols.cs`](../src/TABCoopMod/Core/ObfuscatedSymbols.cs) & [`GameBridge.cs`](../src/TABCoopMod/Core/GameBridge.cs)

本文档与 `ObfuscatedSymbols.cs` 保持同步，记录实测提取的混淆符号（`#=z...`）、引擎明文成员与反编译关键陷阱。新增符号**必须同步维护到 `ObfuscatedSymbols.cs`**。

---

## 1. 内嵌加密程序集清单（Embedded Manifest Resources）

`TheyAreBillions.exe` 的 `<Module>..cctor` 会调用 `#=qfkZ$KkimwzG_5GjOFAkJH7Cbg6LrXJCCJODXq7ULSO4=.#=zX6exa18=()` 注册 `AppDomain.CurrentDomain.AssemblyResolve`，按需从资源解压加载：

| ManifestResource 名称 | 解密后程序集全名 | 导出文件名 | 大小 (Bytes) | 混淆状态 |
| :--- | :--- | :--- | ---: | :--- |
| `b9a9b1ca9f93492687e026fb08597a75` | `DXVision, Version=1.0.0.0` | `DXVision.dll` | 1,233,408 | **全明文** |
| `2e1a46ead7a4460ababa0e338efe2ccb` | `DXPlatform_Desktop, Version=1.0.0.0` | `DXPlatform_Desktop.dll` | 276,992 | 无混淆（封装 SlimDX / OpenTK / BASS） |
| `dd64f0b32b994eb3ba7dd334c48bd40f` | `Steamworks.NET, Version=10.0.0.0` | `Steamworks.NET.dll` | 265,216 | **标准 Steamworks.NET** |
| `3822cc7909fd4dfdac2ffabd4f86c34a` | `SmartPropertyGrid, Version=3.4.0.83` | `SmartPropertyGrid.dll` | 200,704 | 无混淆 |
| `f3c8ae8f19264c499d8f4d10a18c75a2` | *(Eazfuscator VM Bytecode Stream)* | N/A | 101,124 | 虚拟化方法指令流 |

---

## 2. 顶层核心系统类映射（Core Systems & Singletons）

顶层系统类位于全局命名空间，且统一提供静态单例获取方法 **`#=zuartwoo=()`**：

| 逻辑类名 | 混淆后完整类型名 (`ObfuscatedSymbols` 常量) | 基类 | 核心职责与说明 |
| :--- | :--- | :--- | :--- |
| `ZXGame` | `#=z4RevDP3eECXqXS6JRA==` (`Type_ZXGame`) | `DXVision.DXGame` | 游戏主类：系统切换、数据表、存档读写、市长弹窗、过场淡入淡出。 |
| `ZXSystem` | `#=zsW2J3r72Cu83` (`Type_ZXSystem`) | `DXVision.DXSystem` | 所有 ZX 子系统基类。 |
| `ZXSystemGameLevel` | `#=zxRcpu6e7NYzT7tGWqPjpOkc=` (`Type_ZXSystemGameLevel`) | `#=zsW2J3r72Cu83` | 局内核心控制器：鼠标交互、框选、建造、群体移动、HUD 与小地图。 |
| `ZXSystemMainMenu` | `#=zLs$VX1eoAapVflmW_w==` (`Type_ZXSystemMainMenu`) | `#=zsW2J3r72Cu83` | 主菜单系统：管理 `Main` / `New` / `More` 按钮与主菜单加载完成回调。 |
| `ZXSystemCampaign` | `#=zUsJtyOzSfYDnqdeHjCSzszBbekLj` (`Type_ZXSystemCampaign`) | `#=zsW2J3r72Cu83` | 战役大地图系统。 |
| `ZXSystemMapLayers` | `#=zJme8KFhmikprnkg_2CDeiQE=` (`Type_ZXSystemMapLayers`) | `#=zsW2J3r72Cu83` | 地图图层系统（静态单例字段 `#=zQFWYIvs=`）：迷雾、电力网、视野缓冲数组。 |
| `ZXSystemAudio` | `#=zJYbPa4gP2VI16zEh5y2Zxew=` (`Type_ZXSystemAudio`) | `#=zsW2J3r72Cu83` | 音效与语音播放系统（10 个随机选音/播音方法，需与逻辑 RNG 隔离）。 |
| `ZXSystemBuilder` | `#=z5Ns_eH8ENLF7HFU5mg==` (`Type_ZXSystemBuilder`) | `#=zsW2J3r72Cu83` | 建筑施工进度与受击警报系统。 |
| `ZXSteamManager` | `#=z3Zxcp6RwVCZHa9xpeg==` (`Type_ZXSteamManager`) | `System.Object` | Steam 成就与 `SteamAPI.RunCallbacks()`（`#=zVnIv8xS3EOZr()`）。 |
| `ZXMessageBoxHelper` | `#=zzi2cl4h1HYA5cX8aaw==` (`Type_ZXMessageBoxHelper`) | `System.Object` | 原生弹窗工具类。 |
| `ZXFontCatalog` | `#=zqZKzsA47yWa6` (`Type_ZXFontCatalog`) | — | 游戏字体目录。 |
| `TerrainSystem` | `#=zDB$fZzE4fVJzNeDCt$4XVmDIV3Ul` (`Type_TerrainSystem`) | — | 地形资源格系统（`TerrainResourceCells`，供 `BHFarmer` 麦田寻格使用）。 |
| `ZXGameState` | `ZX.ZXGameState` | `System.Object` | 顶层状态（`LevelState`, `CampaignState`, `SurvivalModeParams`, `Difficulty`）。 |
| `ZXLevelState` | `ZX.ZXLevelState` | `ZX.DXGameState` | 局内数据状态（资源、保留量、`LevelEntities`, `Mayors`, `ShowFullMap` 等）。 |

---

## 3. `DXVision.dll`（未混淆引擎层）关键类与坐标/渲染结论

### 3.1 `DXVision.DXGame` & `DXPhysicWorld`
- `DXGame.Current`、`DXGame.IsIADeterministic`（默认 `false`，置 `true` 后单线程顺序更新 `_Entities` 并同步调 `UpdatePhysics`）、`DXGame.SyncLogic`。
- `DXGame.LastGameTimeIAUpdate`、`IAIterationInt`（30Hz 逻辑帧计数）、`GameTime`、`GameSpeed`、`Paused`、`Focused`、`Visible`（注意：**本版本无 `WindowActive` 属性**，窗口激活状态实际通过 `Focused`/`Visible` 控制）。
- 核心生命周期：`StartFrame()`、`SetCamera()`、`UpdateGameLogic()`、`UpdatePhysics()`、`LoadPhysics()` / `LoadPhysicsOld()`、`IsFrameSceneModule(int)`、`InvokeOnStartFrame(Action)`、`ClearEntities()`。
- `DXPhysicWorld`：`Bodies`（含未激活或 `Handle == IntPtr.Zero` 的 `DXPhysicBody.Empty` 占位句柄，排序前需过滤）、`LastGameTimeUpdatePhysics`、`TimeBodyPosition`、`TimeLastBodyPosition`、`Step(1.0 / 12.0)`。

### 3.2 `DXVision.DXScene` & `DXWorldGrid`（等轴测坐标铁律）
- **坐标系区分**：`entity.Position` 为 **WorldPoint（世界网格坐标）**；`DXScene.Current.CameraArea` / `CameraCenter` 为旋转 45° 后的 **ScenePoint（等轴测场景坐标）**。
- 世界坐标转相机设置必须通过 `DXWorldGrid.ScenePointFromWorldPoint(worldCenter)` 并调用 `DXScene.Current.SetCameraCenter(sceneCenter, sceneWidth)`。
- **寻路接口**：`DXWorldGrid.FindPathConcurrent` / `FindPathConcurrentWithHighPriority` 为异步多线程寻路；`DXWorldGrid.FindPath` 为同步寻路。

### 3.3 `DXVision.DXGuid`, `DXRandom`, `DXEntity`, `DXBehaviour<T>`
- `DXGuid.New()`：默认使用 `System.Random` 生成 `[0, 2^62)` 随机 ID。
- `DXRandom`：`DXRandom.Global` 与无参 `new DXRandom()` 均非确定性；内部游标 `mti` 为私有字段，故快照恢复或重种需调用 `new DXRandom(seed)` / `init_genrand(uint)`，不可仅拷贝 `Mt` 数组。
- `DXBehaviour<T>._GlobalRandom`：是 **`[ThreadStatic]` 静态字段**（`T` 常见为 `ZX.Behaviours.ZXBehaviourData` / `BData`），HarmonyX 无法补丁其泛型 getter，必须在逻辑线程内按“线程 + 载入代次”重置。
- **裸 `DXEntity` 与 `Raven`**：所有对局逻辑实体均继承 `ZX.Entities.ZXEntity`；类型为 `typeof(DXEntity)` 的裸实体（如 `#=zg10wm6mM9hhx` 生成的受击飙血/弹花贴图）与 `Raven`（环境飞鸟）仅用于纯视觉表现，不参与对局逻辑。

---

## 4. `TheyAreBillions.exe` 混淆成员完整字典

### 4.1 `ZXGame` (`#=z4RevDP3eECXqXS6JRA==`)

| 混淆名 | 签名 / 类型 | 语义与已知引擎细节 |
| :--- | :--- | :--- |
| `#=zuartwoo=()` | `static ZXGame` | `ZXGame.Current` 单例 |
| `#=zCj$vlp8=` | `List<ZXCommand>` (Field) | 已注册 `ZXCommand` 列表 |
| `#=zBYC_Wew=` | `static string (string key)` | 本地化字符串查询 `Localize` |
| `#=zND5ul2zfzAnWdSnC0A==` | `static string ()` | 存档目录路径 |
| `#=zMtGuEM2lBSlZ5BGWvg==` | `void (string filePath)` | 写 `.zxsav` + `.zxcheck` |
| `#=z3YFOTVtBw_rT7oSQRA==` | `ZXGameState (string filePath)` | 读档反序列化 `ReadSaveFile` |
| `#=zSV0_oCta8rEv` | `void (string, Action, bool, bool)` | 完整存档流程 `SaveGame` |
| `#=z9IpK6P4YlW8GIY7olg==` | `void (string path)` | 加载存档并进图 `LoadGameAndEnter`（**已知原版 Bug**：第 229 行空判写反为 `if (CurrentGameSystem == null) CurrentGameSystem.Enabled = false;`，关卡内二次读档需外部 `PrepareForProgrammaticLoad()` 先关闭旧系统并 `ClearEntities()`） |
| `#=zGQN$8GM48urIpvI5bA==` | `static bool (string path)` | 存档 `.zxcheck` 校验门禁 `IsSaveLoadable`（**校验内容与 `.zxsav` 文件名绑定**） |
| `#=zbvwh$ickzalV` | `DXObject` (Field) | 黑屏淡入淡出遮罩 `LoadingFadeObj` |
| `#=zTkvm$fc5hNvM` | `bool` (Field) | 启动过渡淡入激活标志 `StartupFadeActive` |
| `#=zB3Vhdin5PerfrubjO4AblAA=` | `static bool ()` | `IsMultiplayer()`（仅 6~8 字节 IL，会被 x64 RyuJIT 内联） |
| `#=znDPORUoLfC4p` | `static void (ZXPlayerModeType)` | `SetPlayerMode`（`Singleplayer=0`, `Multiplayer=1`） |
| `#=zcITiOSD6YNmD` | `void ()` | 弹出市长二选一窗口 |
| `#=zx3_1WsJs4Dyck79oqg==` | `void ()` | 查看已选市长列表窗口 |
| `#=zqF2tlklT$8RE` | `void (Action, bool)` | 退回主菜单 `ReturnToMainMenu` |

### 4.2 `ZXSystemMainMenu` (`#=zLs$VX1eoAapVflmW_w==`)

| 混淆名 | 类型 / 签名 | 语义与用途 |
| :--- | :--- | :--- |
| `#=zUM4xl3ca4QS35TzMAg==` | `DXGridLayout` (Field) | 主菜单垂直按钮容器 |
| `#=zyDj$brKr7icb` | `Dictionary<TMenuCategory, List<ZXButtonLink>>` | 主菜单分类按钮字典（`TMenuCategory`: `Main`, `New`, `More`） |
| `#=zBObIu5ds9WvT` | `bool` (Field) | `ReadyForInput`（主菜单初始化就绪标志） |
| `#=zYxOmgpMMO3Yb` | `ZXButtonLink` (Field) | 主菜单「继续 / 读档」按钮 |
| `#=zp8c19tQ=` | `void (TMenuCategory)` | 切换菜单分类 |
| `#=zwK_pUIeRBpbp` | `void ()` | **主线程加载完成回调**（执行 `Scene.Clear` / `Scene.Init` 并置 `ReadyForInput = true`，自动读档钩子与返回主菜单大厅恢复触发点） |
| `#=z90VC8V_3LX2x` | `void (DXButton)` | 「继续/从存档加载」入口，供联机房主复用单人读档列表 |
| `#=zxd3DE94B60Abw_zkg7L488DfPg5s` | `void (DXButton)` | 「新生存游戏」入口，供联机房主复用单人新局向导 |
| `#=zDnLtRmxOqjXPh5h9_Q==` | `void (DXButton)` | 「自定义地图」入口，供联机房主复用单人自定义地图列表 |

### 4.3 `ZXSystemGameLevel` (`#=zxRcpu6e7NYzT7tGWqPjpOkc=`)

| 混淆名 | 类型 / 签名 | 语义与用途 |
| :--- | :--- | :--- |
| `#=z0o_eM4o=` / `#=zsP3wQ40=` | `ZXCommand` / `ZXCommandTarget` | 当前挂起命令与目标结构体 |
| `#=zetgfbFxpsSuV` / `#=zMaiMxP6Lfc1s` / `#=z_QMU7SQiIBfj` | `PointF` / `Point` / `DXEntity` | 鼠标世界浮点坐标 / 网格坐标 / 悬停实体 |
| `#=zZ$S0aaS3NQsgmtkOww==` | `OCellAreaCursor` | 建造网格绿/红光标 |
| `#=z4cHfUtNBoiM8` / `#=zCJSdEwbGvSqa` | `ZXControlCommandsManager` / `SelectionManager` | 命令面板与底部多选栏管理器 |
| `#=zfFXkD0aj1o9gF76ANQ==` | `static bool` (Field) | 开局 6 秒镜头拉近动画标志（`true` 时每帧覆写 `CameraArea`） |
| `#=zmTU4kueQctVr` | `void (DXLevel)` | `InitLevel` |
| `#=zwK_pUIeRBpbp` | `void ()` | 读档完成后主线程收尾回调 `OnLevelLoadComplete` |
| `#=zGxTz6vGn2dxDprCE7A==` / `#=zX9V39sQYkQxr95dJ7A==` | `void (DXObject)` | 场景鼠标右键 / 左键点击处理 |
| `#=zN0jTYBQMVgRf` | `void (bool)` | 执行当前挂起命令 `ExecutePendingCommand` |
| `#=zQp2M56bpPd9jmiNkVA==` / `#=zgn6bDkSd9MGS3$WvAQ==` | `GroupMoveSelected` / `AssignGroupMove` | 群体移动命令与散点分配算法 |
| `#=znoo7TyEtVemrA_J6FQ==` | `void (PointF, ..., float)` | 地面点击指示圈动画 |
| `#=zhWLC3K8=` | `void (string, Color, int)` | 顶部中央横幅提示（`durationMs=0` 时在暂停帧立即 `Opacity=1`） |
| `#=zYWs2Gv9F3GD5` | `void (...)` | 左侧通知队列 |
| `#=zR1qHCYY=` | `void ()` | 刷新右下角命令面板 |
| `#=zbcjZqBFFSY96` | `bool` (Field) | 对局已结束标志 `GameEndedFlag` |
| `#=z40N_lqqB8daJ` | `static DXAnimation` (Field) | 战败红屏模糊 / 局末全屏过渡动画 `ScreenAnim` |
| `#=zIoSB3Q$9ZjUF` | `void (bool won)` | 触发胜负结算 `TriggerGameEnd`（设置 `ZXLevelState.EndStatus` 与结束标志后调用生存/战役结算） |
| `#=zuY2pslm6MYCZNwXKL0J6wWQ=` | `void (bool won, bool isIntroDelay)` | 生存模式胜利/战败结算流程 `ShowSurvivalEnd` |
| `#=z50KAckqpQjxWzdXGV4uLSoo=` | `void (bool won, bool isIntroDelay)` | 战役模式胜利/战败结算流程 `ShowCampaignEnd` |

### 4.4 `ZXCommand`, `Build`, `Train`, `ZXMayor`

| 类名 | 混淆成员名 | 签名 / 类型 | 语义 |
| :--- | :--- | :--- | :--- |
| `ZXCommand` | `#=zqRskexw=` / `#=z4koKvss=` / `#=z5pCZsgI=<T>` | static | 按 ID / 类名 / 泛型获取命令单例 |
| `ZXCommand` | `#=zfmgzkuw=` | `void (ZXEntity, ZXCommandTarget)` | 命令执行核心入口 |
| `ZXCommand` | `#=zKc8Kpc_EaFhf` / `#=zXAX1VeKTLe7O` / `#=zcPu1$70ruiRE` | virtual | `CanExecute` / `IsVisible` / `GetResourcesCost` |
| `ZXCommand` | `#=z9whD4sw=` | `void (ZXEntity, bool)` | 取消当前命令并退款 |
| `Build` / `Train` | `#=zQ4lx63I=` | `static Build/Train (Type)` | 按实体类型获取建造或训练命令 |
| `Build` | `#=zBTwSMLo=` | `static ZXEntity (...)` | 在指定网格放置建筑（市长奖励建筑走 `EntityBonusCreation`） |
| `ZXMayor` | `#=zoTuFkHI=` | `static Dictionary<string, ZXMayor>` | 全局市长注册表（按 `MayorId` 查找） |
| `ZXMayor` | `#=zPTw8CXGE0RqF` | `void (ZXLevelState)` | 应用市长加成 |

### 4.5 实体与组件（`ZXEntity`, `CCommandable`, `CSelectable`, `CMovable`, `CInvisibleWhenNotWatched`）

| 类名 | 混淆成员名 | 语义与用途 |
| :--- | :--- | :--- |
| `ZXEntity` | `#=zkK_YqS85HrT3g75MJrlvMYo=` / `#=zTXZoQFXOaWH7` / `#=zS6TvFuwF_68l` / `#=zCv3n3xlGOMilHuHlHA==` / `#=zcODCID3ur9P9` | 分别获取 `CCommandable` / `CSelectable` / `CLife` / `CMovable` / `ZXEntityDefaultParams` |
| `CCommandable` | `#=zE83e_h4=` / `#=zQKpiMFU=` / `#=zyCiF7Lo=` | 获取/设置当前命令及目标结构体 `Target` |
| `CCommandable` | `#=zRQYIK_o=` / `#=z8g7GC7xLrr8j` / `#=zWGiN6nrTv8Dp` / `#=zrz2EI1A=` | 入队 / 取消队列槽位 / 取消当前命令 / 队列自动推进下一项 |
| `CSelectable` | `#=zIiHCgn_odqH7` / `#=zM3nhRXPdWdhx` / `#=zClknEUY73pQc` | 本地选中列表 / 主选中实体 / 清空选中（**回放侧禁用**） |
| `CMovable` | `#=ziMavkfK9ZJji` | 挂起的异步寻路请求引用 `PendingPathRequest` |
| `CInvisibleWhenNotWatched` | *(明文类)* | 控制非注视区域僵尸 `SceneObject.Opacity = 0f`，依赖 `ZXSystemMapLayers` 前台视野数组 `#=z69ntQguW1qCr` |

### 4.6 `ZXLevelState`, `ZXSystemMapLayers`, `GUI`, `Audio`, `SysColonists`, `BHFarmer`

| 类名 | 混淆成员名 | 语义 |
| :--- | :--- | :--- |
| `ZXLevelState` | `#=zQXHqcVh9mGZZ` / `#=zF1YHTRUZSNRa` | 存档前保存时间 / 读档后恢复时间 |
| `ZXLevelState` | `#=z5YtS5bwlM1kX` / `#=zG0slAM4=` / `#=zh4x6WKNtqKnr` / `#=zOHDY2QTzgRSB` | 扣费 / 退款 / 校验资源 / 获取 `DXLevel` |
| `ZXSystemMapLayers` | `#=zQFWYIvs=` / `#=z69ntQguW1qCr` / `#=z7sWPJNagVkip` / `#=zwJQaMvq$T3G4` | 单例 / 当前视野前台数组 / 20-tick 后台缓冲 / 已探索迷雾数组 |
| `ZXMessageBoxHelper` | `#=zcvkFXLt$qEo6` / `#=zy6vF6BSAUKyT` / `#=zFNDB6pz7Wqg3` / `#=znd_PcbSNuH79` / `#=zYjeCpah9Jpv2S1i8lQ==` / `#=zth32r9N3TXbR` | Alert / OkCancel / YesNo / InputBox / ListSelector / PlainTextRenderer |
| `ZXWindow` | `#=zJMx0W0Q=` / `#=zyruJg$0=` / `#=zePbLN$s=` | 添加底部按钮 / 显示窗口 / 关闭窗口 |
| `OMiniMap` | `#=zvr$ku1Q=` / `#=zi91u4abSRmxU` / `#=zThX9FxI=` | 小地图雷达打点 / 本地白色视野框 `DXObject` / 视野框更新方法 |
| `ZXSystemAudio` | `#=zas_QVHmVp4jw4ljVfQ==` 等 10 个方法（完整见 `ObfuscatedSymbols.cs` §10） | 音频随机选音与播放入口（`AudioRngIsolationPatch` 隔离 RNG） |
| `SysColonists` | `#=zUJ8sfNZq6x5U` / `#=zTsexjauQRm5IZtGeraXJooM=` / `#=zt0FcM$Mg6$EPDdIcLavKxd4=` | 游标 / 列表 / 重建列表 |
| `BHFarmer` / `ZXBehaviour` | `#=zK2CGzIZdW6uI$Q9jPNxMDPs=` / `#=zHKPHAW92Cyls` | 农场麦田寻格与 `BData` 所属实体获取 |

### 4.7 生存新局生成、过渡切换与全图全景（同步 `ObfuscatedSymbols.cs` §15）

| 类名 | 混淆成员名 | 语义与用途 |
| :--- | :--- | :--- |
| `MapGenerator` (`#=zyl_NPjjlA7DRfVtsRJCX1kN4BxSr`) | `#=zEzgd90E=` | 随机生存地图生成入口（`ZXRandomLevelParams`）；含非 2 的幂次地图升格 `512` 网格逻辑 |
| `ZXMapTheme` | `#=z4k5FO$EclQhr` | 静态获取全生态主题配置字典/集合 |
| `ZXLevelEvent` | `#=z0XSLMcBEuHC4` | 独立尸潮刷怪波次人口倍率缩放入口 |
| `ZXGame` / `ProgressWindowHelper` | `#=z9DPDdq9qP9lZ` / `#=zzi2cl4h1HYA5cX8aaw==.#=z9gBNlmJCE1ly` | `AsyncTransition` 与异步进度窗（自动化无 UI 切图流程安全等待用） |
| `ZXSystemGameLevel` | `#=z5NVIUf3bnsHNNo8FfA==` / `#=zwK_pUIeRBpbp` | 原生整图全屏全景导出（`CapturePanorama`）与进入关卡视图入口 |
