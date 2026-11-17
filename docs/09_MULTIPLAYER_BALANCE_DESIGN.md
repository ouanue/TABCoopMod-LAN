# 09 — 多人执政官模式（Archon Mode）生存新局平衡性设计论证与留档

> **整理日期**: 2026-11-14（2026-11-15 更新实装、IL 边界修正与全图对比结果）
> **状态**: 已实装并完成双开与 4 场景全图视觉验收（默认 `Co-op Balanced`，可在等候大厅切换 `Vanilla`）
> **适用范围**: 仅针对多人联机下 **新建生存模式（New Survival Game）**；**现有存档加载（Load Save）** 与 **自定义地图（Custom Map）** 维持 $100\%$ 原版不做改动。

---

## 1. 适用范围与核心设计原则

### 1.1 模式边界划分
1. **存档加载（Load Save）—— 零改动**：
   - 既有存档的地形网格、资源点与实体列表已经固化，联机模式下仅允许 2~3 名玩家共同进入操作，不注入额外单位或修改地图参数。
2. **自定义地图（Custom Map）—— 零改动**：
   - 创意工坊及自定义关卡通常包含作者预设的触发脚本、固定刷怪波次与严格的兵力/资源配平约束，保持原汁原味是最稳妥的选择。
3. **新建生存模式（New Survival Game）—— 联机均衡调整**：
   - **解决痛点**：《亿万僵尸》（TAB）原版生存开局仅提供 `4 游侠 + 1 士兵`，在 2~3 人共控的前 5~8 分钟（兵营建成前）容易出现“只有一人有兵可拉，其余队友只能干看或互相抢兵”的冷场期；同时多人并行 APM 推进更快，原版 `256×256` 地图与资源规模会过早触边。
   - **设计目标**：在不破坏原版前期路口防守紧迫感的前提下，让每位玩家开局都有独立可操作的少量单位，并按人数微调扩大地图尺寸与可用资源、对应匹配游荡僵尸与尸潮强度。
   - **可切换选项（后续实装建议）**：在 `CoopLobbyWindow`（等候大厅）为房主提供模式开关：
     - `联机均衡 (Co-op Balanced)`（默认）：启用本文档推导的多人平衡缩放。
     - `原版纯净 (Vanilla)`：完全沿用原版 `256×256` 地图与初始单位，供追求原版双人极限挑战的玩家选用。

### 1.2 架构零同步负担（天然防 Desync）
本 Mod 联机开局采用 **「房主进图完成瞬间导出内存快照（`coop_host_snapshot.zxsav`）直传客机」** 的机制：
- 地图生成、单位增发与 `ZXLevelEvent` 事件缩放均只在**房主（Host）新建关卡生成期执行一次**。
- 客机通过快照直接还原完全一致的 `DXLevel` 尺寸、实体 ID、初始兵力与事件队列，**无需新增任何网络同步协议，天然不会破坏确定性 Lockstep**。

---

## 2. 引擎底层相关代码结构（反编译实测结论）

基于 `scripts/decompile_type.ps1` 对 `TheyAreBillions.exe` (v1.0.14.29) 的反编译结果，与生存模式生成和平衡相关的核心类与成员如下：

### 2.1 生存模式参数：`ZX.GameSystems.ZXRandomLevelParams`
在 `ZXSystemMainMenu.#=z6w8Ud0YvE0T9SBLdG1_stiY=`（新建生存游戏向导）点击确认后，会构造或读取 `ZXRandomLevelParams` 并强制置 `NCells = 256`，随后传入随机地图生成器 `#=zyl_NPjjlA7DRfVtsRJCX1kN4BxSr.#=zEzgd90E=(ZXRandomLevelParams)`：

| 字段 / 属性 | 类型 | 原版行为说明 |
| :--- | :--- | :--- |
| `NCells` | `int` | 地图有效边长，原版新生存游戏向导硬编码为 `256`。 |
| `Seed` | `int` | 地图生成随机种子（已由 `CoopSessionManager` 接管滚动）。 |
| `FactorGameDuration` | `float` | 游玩天数倍率（`1.5` = 150 天，`1.2` = 120 天，`1.0` = 100 天，`0.8` = 80 天）。 |
| `FactorZombiePopulation` | `float` | 僵尸数量/难度系数（`0.2` / `0.5` / `0.8` / `1.0` / `1.2` / `1.5`）。 |
| `ScoreFactorFixed` | `float?` | 可选覆写的固定难度得分系数。 |

### 2.2 地图尺寸 `NCells` 与 `FactorPlayableArea` 裁剪机制
在 `#=zyl_NPjjlA7DRfVtsRJCX1kN4BxSr.#=zEzgd90E=` 开头，引擎原生支持 `128 ~ 512` 之间任意非 2 的幂次边长：
1. 若传入 `256 < NCells <= 512`（例如 `280` 或 `304`），引擎将底层网格边长 `nCells` 对齐到 `512`，同时计算有效可玩比例：
   $$\text{num} = \frac{\text{NCells}}{512f}$$
2. 调用 `DXWorldGrid.Create(512, 0, 0, usePathFinding: false)`，并通过 `DXWorldGrid.GetSceneSquareArea(nCells, num).Enlarge(1f)` 得到有效可玩矩形 `rectangleF`。
3. 矩形框外的所有格子标记为 `dXNoyseLayer[x, y] = 1.0`（不可通行屏障），并将 `dXLevel.NCells = nCells`、`dXLevel.FactorPlayableArea = num` 写入关卡。
4. 地图四周的 4 个尸潮刷怪条带（`#=zuJRpnkt4f2Fr`）直接读取 `dXLevel.PlayableArea`，因此会**自动贴合扩大后的有效边界**。

**各类地图要素随 `NCells` 扩大的行为差异**：

| 要素类别 | 是否随 `NCells` 自动按面积放大 | 底层依据与注意事项 |
| :--- | :---: | :--- |
| 陆地、草地、森林、石矿、铁矿、金矿 | **是（自动放大）** | 在有效矩形 `rectangleF` 内逐格按柏林噪声生成。 |
| 油井（`OilSource`） | **是（近似按面积放大）** | 按 `NCellsPerOilSource`（默认 `48`）分块遍历有效可通行格生成。 |
| 普通游荡僵尸（`LayerZombies`） | **是（自动放大）** | 遍历 `0..nCells` 有效连通格，按 `num43 = zXMapTheme.InfectedFactor * FactorZombiePopulation` 生成。 |
| 感染者村庄（Doom Village） | **否（固定配置）** | 数量 `num9` 与半径 `num11` 均直接取自 `ZXMapTheme.NumDoomVillages` 与 `DoomVillagesSize`。**已解决工程细节**：源码中村庄距基地最大轴距上限为 `nCells / 2 - 60 - num11`（同时渡鸦/遗迹上限为 `nCells / 4`）；当引擎把 `280`/`304` 对齐到 `512` 时未缩放的 `nCells` 会放大至 `196 - num11` 而失效，现已通过 [`SurvivalBalancePatch`](../src/TABCoopMod/Patches/SurvivalBalancePatch.cs) Transpiler 按 `TargetNCells` 等比缩回至 `74`（2P）/ `81`（3P）。 |
| 拾取宝箱 & 食物/电力废墟 | **否（固定配置）** | 直接读取 `ZXMapTheme.NumTreasures`、`NumSuppyFood`、`NumSuppyEnergy`。 |
| 巨型感染者 & 变异体 | **否（固定配置）** | 读取 `ZXMapTheme.NumInfectedGiants` 与 `NumInfectedMutants`，仅随 `FactorZombiePopulation` 阶梯翻倍。 |

### 2.3 初始兵力生成与基地资源补偿机制
1. **初始部队生成位置**：在 `#=zEzgd90E=` 第 604~617 行中，原版固定循环生成：
   - `4` 名游侠（Ranger，模板 `#=zYLpZK4K9zSHp.#=zZXhftyg=.#=znTF4_8nBxyGcPejHkw==()`）
   - `1` 名士兵（Soldier，模板 `#=zYLpZK4K9zSHp.#=zZXhftyg=.#=z$miT0UOuLwmfejZ2dw==()`）
   - 通过 `dXNoyseLayer13.GetCellNearPoint(cc.CellArea.Right, cc.CellArea.Bottom, 1.0)` 选点落位并加入 `zXMapDrawer.ExtraEntities`。
2. **单位维护占用计算**：`ZX.ZXLevelState.#=zBpC6By32LAqv()` 每逻辑周期遍历所有存活的 `Human`（`Team == ZXTeamType.Player`），动态累加 `WorkersNeeded`、`FoodNeeded` 与 `-GoldGen`：
   - **游侠（Ranger）**：`WorkersNeeded = 1`，`FoodNeeded = 0`，`GoldGen = -1`（维护费 1 金/8h）。
   - **士兵（Soldier）**：`WorkersNeeded = 1`，`FoodNeeded = 1`，`GoldGen = -3`（维护费 3 金/8h）。
3. **无痛资源补偿通道**：在 `ZX.Entities.Building.#=z3rO8Jok=()`（第 373~378 行）中，指挥中心（`CommandCenter`）原生会读取随 `ZXLevelState` 序列化的三个字段：
   - `ZXLevelState.CommandCenterExtraFood`
   - `ZXLevelState.CommandCenterExtraWorkers`
   - `ZXLevelState.CommandCenterExtraEnergy`
   因此，只需把新增初始单位占用的工人与食物数量同步加到 `CommandCenterExtraWorkers` 与 `CommandCenterExtraFood` 上，即可保证开局可用工人和食物与单人原版 **100% 一致**，不会导致 Day 1 无法起帐篷或伐木场。

### 2.4 游荡僵尸与尸潮（Swarm）缩放接口
- **游荡僵尸密度**：由生成器第 626 行 `num43 = zXMapTheme.InfectedFactor * (double)params.FactorZombiePopulation * 1.0` 控制。
- **独立尸潮事件**：生成器第 808~959 行将 **最终尸潮（`FinalSwarm = true`）**、**前期/中后期周期尸潮（`ZXLevelEvent`，`MaxRepetitions = 4` 或 `5`）** 及 **无倒计时零散骚扰波（`MaxRepetitions = 10`）** 分别实例化，随后统一调用 `levelEvent.#=z0XSLMcBEuHC4(factorPopulation, 100)` 按倍率缩放各事件词条的刷怪上下限 `[A, B]`。这使我们可以将 **地图游荡密度**、**前期尸潮** 与 **中后期/最终尸潮** 拆开进行独立倍率调节。

---

## 3. 数学建模与配平推导（保守均衡版）

设联机执政官模式下的玩家人数为 $N \in \{1, 2, 3\}$。

### 3.1 路口拓扑约束与保守初始兵力方案
在《亿万僵尸》常规随机地形中，指挥中心受山脉、林区与水域切割，开局向外延伸的天然防御通道通常仅有 **$2 \sim 3$ 条主路口**。
- 若初始兵力发得过多（例如翻倍发到 8~11 个兵），每个路口放 1 名哨兵预警后仍余大量机动单位，会显著稀释前期“视野盲区漏怪”的紧张感。
- 为兼顾 **“所有玩家开局都有单位可操”** 与 **“保留前期防守压力”**，采用保守增发策略，以游侠当量（$\text{RE}$，1 游侠 = $1.0\text{ RE}$，1 士兵 = $2.0\text{ RE}$）度量初始战力：

| 玩家数 $N$ | 初始单位配置 | 较单人增量 | 初始战力 $P_{\text{init}}(N)$ | 初始战力倍率 $\gamma_U(N)$ | 开局分兵与路口压力体感 |
| :---: | :--- | :--- | :---: | :---: | :--- |
| **$N=1$** | `4 游侠 + 1 士兵`（5 单位） | 基准 | $6.0\text{ RE}$ | $1.000\times$ | 单人原版基准 |
| **$N=2$** | **`6 游侠 + 1 士兵`**（7 单位） | **`+2 游侠`** | $8.0\text{ RE}$ | **$1.333\times$** | 留少量游侠盯防次要路口后，P1 (`2游侠+1士兵`) 与 P2 (`2游侠`) 可同时双线试探清图 |
| **$N=3$** | **`6 游侠 + 2 士兵`**（8 单位） | **`+2 游侠 + 1 士兵`** | $10.0\text{ RE}$ | **$1.667\times$** | 刚好分为 `2游侠+1士兵` / `2游侠+1士兵` / `2游侠` 三支小组，三人各管一路且无冗余哨兵 |

> **工程实现细节**：额外生成的单位建议围绕指挥中心按玩家数量错开 $3 \sim 4$ 格**分簇生成**，减少开局拉框选兵时的重叠冲突；同时为 `CommandCenterExtraWorkers` 补偿 $+2$（2 人）/ $+3$（3 人），为 `CommandCenterExtraFood` 补偿 $+0$（2 人）/ $+1$（3 人）。

### 3.2 径向清图立方-平方模型与地图尺寸推导
1. **前中期有效清图输出倍率 $\Lambda_{\text{clear}}(N)$**：
   - 多人分工下（1 人兼顾家内建造与电网规划，其余玩家专注前线走 A 避免单位发呆），保守兵力带来的前线微操利用率增益为 $\eta_{\text{micro}}(2) \approx 1.15$、$\eta_{\text{micro}}(3) \approx 1.25$。
   - 因此前中期团队总清图输出倍率为：
     $$\Lambda_{\text{clear}}(2) = 1.333 \times 1.15 \approx \mathbf{1.53\times}, \qquad \Lambda_{\text{clear}}(3) = 1.667 \times 1.25 \approx \mathbf{2.08\times}$$

2. **径向清图积分（外圈僵尸强度随半径递增）**：
   - 由反编译的 `ZXMapTheme`（`MinDistanceForWeak/Medium/Strong/PowerfulInfected` $= 28 / 44 / 62 / 75$ 格）可知，地图游荡僵尸的综合血量密度 $\rho_{\text{HP}}(r)$ 随距基地半径 $r$ 近似呈线性递增：$\rho_{\text{HP}}(r) \propto r$。
   - 推平半径 $R$ 所需的累计总输出 $W(R)$ 满足：
     $$W(R) = \int_{r_0}^{R} 2\pi r \cdot \rho_{\text{HP}}(r)\,dr \propto \int_{r_0}^{R} r^2\,dr \propto R^3$$
   - 由此得相同时间内的清图半径增长倍率：
     $$\frac{R(2)}{R(1)} = 1.53^{1/3} \approx \mathbf{1.152\times}, \qquad \frac{R(3)}{R(1)} = 2.08^{1/3} \approx \mathbf{1.277\times}$$

3. **地图边长 `NCells` 定档（每增 1 人 `+24` 格）**：
   - 取理论清图半径增长率的约 $70\%$ 作为边长放大率（其余增长由略微提高的游荡僵尸密度吸收），使地图有效面积与自然资源总量适度扩大而不过度拉长跑图距离：
     - **$N=1$**：`NCells = 256`（边长 $1.000\times$，面积 $1.00\times$）
     - **$N=2$**：**`NCells = 280`**（边长 $k_{\text{map}}(2) = 280/256 \approx 1.094\times$，**有效面积与矿木资源总量 $= 1.094^2 \approx \mathbf{1.20\times\ (+20\%)}$**）
     - **$N=3$**：**`NCells = 304`**（边长 $k_{\text{map}}(3) = 304/256 \approx 1.188\times$，**有效面积与矿木资源总量 $= 1.188^2 \approx \mathbf{1.41\times\ (+41\%)}$**）

### 3.3 感染者村庄几何模型：扩大半径而非增加数量
在 `Doom Village` 生成逻辑（`#=zEzgd90E=` 第 204~299 行）中，若对 `NumDoomVillages` 采用向上取整（$1 \to 2$），会导致 800%/900% 难度（通常仅 1 座巨型城镇）凭空多出一座死城，直接翻倍压迫空间并可能堵死主要扩展通道。
- **圆盘面积律**：每个感染者村庄是以随机半径 `num11` 构成的二维圆盘，圆内塞入的感染者房屋数量 $N_{\text{house}}$ 与圆盘面积成正比：
  $$N_{\text{house}} \propto \pi \cdot \text{num11}^2$$
- **修正方案**：**完全保持村庄数量 `num9` 不变**，仅将单个村庄的半径 `num11` 按地图边长缩放系数 $k_{\text{map}}(N)$ 放大：
  $$\text{num11}' = \text{round}\bigl(\text{num11} \times k_{\text{map}}(N)\bigr)$$
  如此一来，900% 难度依然是 1 座大镇，但其占地面积、内部感染建筑数、总刷怪量与拆除收益**严格随地图面积按 $k_{\text{map}}^2$（$+20\% / +41\%$）同比扩大**，全图村庄占比保持不变。

### 3.4 殖民地复利经济雪球与分段难度补偿
结合前期保守兵力与中期大图资源的复利转化，团队在后期（最终尸潮前）的实际产能与军力规模相对单人原版约为 **$1.20\times \sim 1.25\times$（双人）** 与 **$1.40\times \sim 1.45\times$（三人）**。为此采用如下分维度补偿：

1. **游荡僵尸（Roaming）—— 维持家门口体感，自然放大全图总量**：
   - 将局部噪声密度系数仅轻微上浮 **$1.05\times$（2 人）** 和 **$1.10\times$（3 人）**，确保前 10 天家门口局部密度与原版几乎无异。
   - 叠加地图面积放大倍数 $k_{\text{map}}^2$ 后，全图游荡僵尸总量自动达到：
     $$Z_{\text{total}}(2) = 1.20 \times 1.05 = \mathbf{1.26\times\ (+26\%)}, \qquad Z_{\text{total}}(3) = 1.41 \times 1.10 = \mathbf{1.55\times\ (+55\%)}$$
2. **巨人 / 变异体 / 废墟宝箱 —— 按面积四舍五入**：
   - 对 `NumInfectedGiants`、`NumInfectedMutants`、`NumTreasures`、`NumSuppyFood`、`NumSuppyEnergy` 按有效面积倍率 `1.20` / `1.41` 做 **`Math.Round` 四舍五入**（例如 `1 × 1.20 -> 1`，避免单只高危 Boss 因向上取整直接翻倍）。
3. **周期尸潮 & 最终尸潮 ——「前轻后重」分段缩放**：
   - 借助 `ZXLevelEvent.#=z0XSLMcBEuHC4` 对不同时间阶段的尸潮独立缩放，贴合殖民地前期尚未吃满大图红利、后期全额兑现的复利曲线：
     - **前期尸潮（前 35 天）**：**$+10\%$（2 人）/ $+20\%$（3 人）**
     - **中后期尸潮 & 最终尸潮**：**$+20\%$（2 人）/ $+40\%$（3 人）**，与地图面积和经济上限增量（$+20\% / +41\%$）严格对齐。

---

## 4. 最终配平参数总表（定稿基准）

以下参数仅在多人模式新建生存游戏且启用「联机均衡」预设时生效：

| 平衡维度 | 底层实现落点 | 单人原版 ($N=1$) | **双人执政官 ($N=2$)** | **三人执政官 ($N=3$)** | 效果与依据说明 |
| :--- | :--- | :---: | :---: | :---: | :--- |
| **1. 初始兵力** | `#=zEzgd90E=` 开局实体生成 | `4 游侠 + 1 士兵`<br>*(5 单位 / 6 RE)* | **`6 游侠 + 1 士兵`**<br>*(+2 游侠 / 8 RE)* | **`6 游侠 + 2 士兵`**<br>*(+2 游侠 + 1 士兵 / 10 RE)* | 守住 2~3 个路口之余，每人有 2~3 个机动单位操作，保留前期紧迫感 |
| **2. 基地补给补偿** | `CommandCenterExtraWorkers` / `ExtraFood` | `+0 工人 / +0 食物` | **`+2 工人 / +0 食物`** | **`+3 工人 / +1 食物`** | 精确抵消新增初始兵占用，开局可用工人与食物与单人一致 |
| **3. 地图有效边长** | `ZXRandomLevelParams.NCells` | `256` | **`280`** ($+9.4\%$) | **`304`** ($+18.8\%$) | 步长 `+24`，自动触发 `512` 网格裁剪与边界刷怪框适配 |
| **4. 有效面积 / 矿木总量** | 随 `NCells` 平方自动扩展 | $1.00\times$ | **$1.20\times\ (+20\%)$** | **$1.41\times\ (+41\%)$** | 抵消多人多线清图加速，提供对应中后期矿木与绿地容量 |
| **5. 感染者村庄** | 保持 `NumDoomVillages` 不变，仅半径 `num11` 乘以 $k_{\text{map}}$ | 数量 $1\times$<br>半径 $1.000\times$ | 数量 **$1\times$ (不变)**<br>半径 **$1.094\times$** | 数量 **$1\times$ (不变)**<br>半径 **$1.188\times$** | 维持拓扑（如 900% 仍为 1 座大镇），占地与建筑数量同步放大 $+20\% / +41\%$ |
| **6. 游荡僵尸** | 噪声密度阈值 $\times 1.05 / 1.10$ | 密度 $1.00\times$<br>总量 $1.00\times$ | 密度 **$1.05\times$**<br>全图总量 **$1.26\times$** | 密度 **$1.10\times$**<br>全图总量 **$1.55\times$** | 保持开局家门口密度手感不变，全图总怪量随地图尺寸与难度自然放大 |
| **7. 巨人 / 变异体 / 废墟** | 按面积倍率四舍五入 (`Math.Round`) | $1.00\times$ | **$\times 1.20$** (四舍五入) | **$\times 1.41$** (四舍五入) | 避免单数精英怪向上取整直接翻倍 |
| **8. 尸潮强度** | `ZXLevelEvent.#=z0XSLMcBEuHC4` | $1.00\times$ | 前期 **$+10\%$**<br>中后期/最终 **$+20\%$** | 前期 **$+20\%$**<br>中后期/最终 **$+40\%$** | 前轻后重分段缩放，精确对齐复利雪球与地图资源增幅 |

---

## 5. 实装落点、边界修正与实测对比结果（2026-11-15）

### 5.1 核心实现文件
- [`Sync/CoopBalanceManager.cs`](../src/TABCoopMod/Sync/CoopBalanceManager.cs)：负责档案计算（2P `NCells=280` / 3P `NCells=304`）、生成期 `ZXMapTheme` 临时改写与 `finally` 还原、按面积缩放的游荡密度/精英/宝箱、半径放大的 `DoomVillagesSize`、`+2R` / `+2R+1S` 分簇出兵、`CommandCenterExtraWorkers/Food` 补偿与前后段 `ZXLevelEvent` 缩放。
- [`Patches/SurvivalBalancePatch.cs`](../src/TABCoopMod/Patches/SurvivalBalancePatch.cs)：在房主新建生存生成阶段挂载 `Prefix`/`Postfix`/`Finalizer` 驱动，并通过 `[HarmonyTranspiler]`（实机日志 `doom=1, raven=1`）将 `MapGenerator.#=zEzgd90E=` 在 `gridNCells=512` 下的未缩放外环距离上限重写为按 `TargetNCells` 等比缩放的基准值（`GetEffectiveDoomVillageMaxDistBase`：`280 -> 74`、`304 -> 81`；`GetEffectiveQuarterNCells`：`280 -> 70`、`304 -> 76`）。
- [`UI/CoopLobbyWindow.cs`](../src/TABCoopMod/UI/CoopLobbyWindow.cs) 与 [`Sync/CoopSessionManager.cs`](../src/TABCoopMod/Sync/CoopSessionManager.cs)：等候大厅新增房主可切换、客机只读同步的预设按钮（`Co-op Balanced` / `Vanilla`）。

### 5.2 同一种子全图对比实测（`reports/panorama_comparison.html`）
使用 [`scripts/generate_panorama_comparisons.ps1`](../scripts/generate_panorama_comparisons.ps1) 生成同一种子下 Vanilla 与 Co-op 的全图全景对比与定量指标（见 [`reports/panorama_metrics.json`](../reports/panorama_metrics.json) 与 [`reports/panorama_comparison.html`](../reports/panorama_comparison.html)）：

| 场景 (`Seed`) | 档位 (`NCells` / 可玩宽) | 游荡僵尸总量 | 感染村镇建筑 (`DV`) | 关键变化摘要 |
| :--- | :--- | :---: | :---: | :--- |
| **BR 100% 暗黑荒原** (`202601`) | Vanilla `256` (`148`) | `5,198` | `7` | 基准地形与中环感染镇 |
| **BR 100% 暗黑荒原** (`202601`) | **2P `280` (`164`)** / **3P `304` (`178`)** | `9,849` / `11,485` | **`7` / `7`** | Transpiler 修正前 2P 因贴边被截断为 `4`，修正后恢复完整 `7` 座中环建筑 |
| **DS 120% 干旱高地** (`202602`) | Vanilla `256` vs **2P `280`** | `13,067` -> `17,370` | `64` -> `55` | 三座高密村镇均自然分布于中环（修正前被推到边界角落），石矿 `417 -> 602` |
| **FA 120% 寒冰荒原** (`202603`) | Vanilla `256` vs **3P `304`** | `9,769` -> `17,448` | `3` -> `7` | 森林 `3,613 -> 5,706`，石/铁矿近翻倍，村镇位于中环内部 |
| **VO 900% 剧毒荒原** (`202604`) | Vanilla `256` vs **2P `280`** / **3P `304`** | `15,652` -> `22,295` / `25,952` | `81` -> `95` / `115` | 保持单座巨型僵尸城视觉格局并温和扩大规模，且稳定位于可建造边界内 |

### 5.3 双开端到端回归
[`scripts/verify_new_survival_balance.ps1`](../scripts/verify_new_survival_balance.ps1)（`exit 0`）验证：
- Host / Client 联机开启 2P Balanced 新生存局，两端同步校验 `NCells=280, levelNCells=512, extraW=2, extraF=0, Units=6R+1S+1CC`，解停运行后 `StateChecksum` 连续 `MATCH`；
- 退回大厅切换为 `Vanilla` 预设重开，两端一致校验 `NCells=256, extraW=0, extraF=0, Units=4R+1S+1CC`，Host/Client 全程无 `ERROR`。
