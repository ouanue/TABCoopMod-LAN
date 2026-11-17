# 07 — 自动化测试台、回放验证与 MCP 操作指南 (Testing & MCP Guide)

> **核心组件**:
> - 游戏内控制与截图服务：[`ControlServer.cs`](../src/TABCoopMod/Testing/ControlServer.cs)、[`ScreenshotService.cs`](../src/TABCoopMod/Testing/ScreenshotService.cs)、[`CameraProbe.cs`](../src/TABCoopMod/Testing/CameraProbe.cs)
> - 确定性回放与脚本驱动：[`DeterministicReplayVerifier.cs`](../src/TABCoopMod/Testing/DeterministicReplayVerifier.cs)、[`ScriptedCommandDriver.cs`](../src/TABCoopMod/Testing/ScriptedCommandDriver.cs)、[`TestingLifecyclePatches.cs`](../src/TABCoopMod/Testing/TestingLifecyclePatches.cs)
> - 独立 MCP 服务端：[`src/TABCoopMod.Mcp/`](../src/TABCoopMod.Mcp/)（`net10.0` stdio JSON-RPC，无第三方依赖）

---

## 1. MCP 控制通道架构与配置

```text
┌────────────────────┐   MCP (stdio, JSON-RPC)   ┌──────────────────────────┐
│ Antigravity /       │ ────────────────────────> │ TABCoopMod.Mcp (net10)   │
│ Claude Code         │ <──────────────────────── │  独立进程，stdio 服务端    │
└────────────────────┘                           └───────────┬──────────────┘
                                                              │ TCP 127.0.0.1:27901
                                                              │ 换行分隔 JSON
                                                              v
                                                 ┌──────────────────────────┐
                                                 │ TheyAreBillions.exe      │
                                                 │  ├ ControlServer (TCP)   │
                                                 │  ├ 主线程 StartFrame 队列 │
                                                 │  ├ CommandExecutor/NetCommand │
                                                 │  └ ScreenshotService(DXGame.CaptureScreen) │
                                                 └──────────────────────────┘
```

- **线程安全**：socket 线程仅负责收发，所有操作排队至渲染主线程 `DXGame.StartFrame` 执行，并通过 `CommandExecutor`/`NetCommand` 走与联机完全一致的无状态回放路径。
- **构建与部署**：
  ```powershell
  # 1) 构建并部署 Mod
  powershell -ExecutionPolicy Bypass -File scripts/install_mod.ps1

  # 2) 构建独立 MCP 服务端（注意：若 IDE 正占用 TABCoopMod.Mcp.dll，需先断开对应 MCP 进程再编译）
  powershell -ExecutionPolicy Bypass -File scripts/build_mcp_server.ps1
  ```
- **客户端配置位置**：
  - **Antigravity**：`.agents/plugins/tabcoop-mod/mcp_config.json`、`.agents/mcp_config.json` 及 `~/.gemini/config/mcp_config.json`
  - **Claude Code**：仓库根 [`.mcp.json`](../.mcp.json)

### 1.1 启动带控制通道的游戏

```powershell
# 自动读档进入关卡（推荐）
powershell -ExecutionPolicy Bypass -File scripts/run_game_with_control.ps1 `
    -SavePath "$env:USERPROFILE\Documents\My Games\They Are Billions\Saves\6666.zxsav"

# 手动进游戏后再开控制通道
powershell -ExecutionPolicy Bypass -File scripts/run_game_with_control.ps1 -NoAutoLoad

# 冒烟自检（不经过 MCP 层）
powershell -ExecutionPolicy Bypass -File scripts/control_smoke.ps1 -Port 27901
```

> 控制通道通过 `TABCOOP_CONTROL_PORT=27901` 开启，默认不触发回放验证器；关闭自动截图可传 `-AutoShotOff`（`TABCOOP_AUTO_SHOT=0`）。

---

## 2. 17 个 MCP 工具清单

| 工具 | 说明 | 主要参数 |
| :--- | :--- | :--- |
| `game_state` | 当前 tick、暂停、资源、实体数、游戏模式/难度 | — |
| `list_entities` | 列出实体（id/类型/坐标/HP/阵营/可移动） | `typeContains`, `onlyPlayer`, `movable`, `includeProjectiles`, `limit` |
| `move_units` | 群体移动 | `ids[]`, `x`, `y`, `shift?` |
| `place_building` | 放置建筑（走正常 `Build` 命令扣资源） | `buildingType`, `x`, `y`, `sizeW?`, `sizeH?`, `builderId?`, `isBonus?`, `instant?` |
| `train_unit` | 兵营/生产建筑训练单位 | `barracksId`, `unitType` |
| `cancel_current_order` | 取消当前命令/建造 | `ids[]` |
| `cancel_queued_order` | 取消生产队列槽位 | `ids[]`, `slot` |
| `set_paused` | 暂停/恢复 | `paused` |
| `set_game_speed` | 设置模拟倍速（0=暂停，1=正常，最高 20） | `speed` |
| `request_pause` | 同步暂停/恢复；恢复走 3 秒顶居中倒计时 | `paused` |
| `list_mayors` | 列出市长注册表（id/name/level） | `level?` |
| `mayor_select` | 按 `MayorId` 确定性应用市长 | `mayorId` |
| `issue_command` | 原始 `NetCommand` 逃生口 | `type`, `commandId`, `ids[]`, `targetEntityId`, `x`, `y`… |
| `screenshot` | 立即截图并返回 PNG 路径 | `tag?` |
| `get_camera` | 当前相机世界矩形与屏幕尺寸 | — |
| `set_camera` | 按世界坐标/实体移动并缩放相机（`ScenePointFromWorldPoint` + `SetCameraCenter`） | `x`, `y`, `w`, `h`, `zoom`, `entityId` |
| `reveal_map` | 开全图去黑雾并立即显形所有僵尸（仅影响渲染，不改模拟） | `enabled?` |

- **64 位实体 ID**：工具统一以字符串返回（避免 JS 精度丢失），传参字符串/数字均可。
- **截图目录**：默认写入工作区 `tabcoop_shots/`（由 `TABCOOP_SHOT_DIR` 指定，已 `.gitignore`），改动类工具会在延迟约 2 帧等待截图落盘后返回路径。

---

## 3. 自动化确定性回放与双端联机脚本

### 3.1 双进程确定性回放验证（Gate A）
```powershell
# 战役档 120 tick 验证
powershell -ExecutionPolicy Bypass -File scripts/run_replay_verify.ps1 `
    -SavePath "$env:USERPROFILE\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav" -Ticks 120

# 生存大档 180 tick 验证（可选 -ShotCam "cx,cy,w,h" 与 -RevealMap 采集可视化截图）
powershell -ExecutionPolicy Bypass -File scripts/run_replay_verify.ps1 `
    -SavePath "$env:USERPROFILE\Documents\My Games\They Are Billions\Saves\6666.zxsav" -Ticks 180
```
- 辅助对比脚本：
  - `scripts/compare_hashes.ps1`：逐 tick 比对 `coop_replay_run1/2.timeline` 的 `hash/entities/pos/life/proj/prod/resv`。
  - `scripts/compare_pos.ps1`：逐实体对比 `coop_pos_run1/2.txt`（支持排查首个坐标分叉）。
  - `scripts/build_divergence_report.py`：若出现分叉，可结合 `tabcoop_shots/latest_replay_shots.json` 生成自包含 HTML 分叉可视化报告。

### 3.2 双实例 Loopback 与 Chaos 故障注入验证（Gate B）
```powershell
# 洁净回环 65s 同步验证
powershell -ExecutionPolicy Bypass -File scripts/run_chaos_two_instance.ps1 `
    -SavePath "$env:USERPROFILE\Documents\My Games\They Are Billions\Saves\6666.zxsav" `
    -Chaos "" -TimeoutSeconds 90 -HoldSeconds 65

# 带延迟 / 抖动 / 丢包 / 断连的故障注入压测
powershell -ExecutionPolicy Bypass -File scripts/run_chaos_two_instance.ps1 `
    -SavePath "$env:USERPROFILE\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav" `
    -Chaos "latency=120;jitter=30;loss=0.02;disconnect=4000-5000;seed=42" -TimeoutSeconds 90 -HoldSeconds 60
```
- 日志自动分离为 `<游戏目录>\TABCoopMod.host.log` 与 `TABCoopMod.client.log`。

---

## 4. 本机实测测试存档清单

| 存档 | 模式 | 基准 Tick | 总实体 / 玩家实体 | 适用测试场景 |
| :--- | :--- | ---: | :--- | :--- |
| `1_Backup.zxsav` | **Campaign** (Normal) | 31904 | ~9.8k / 178 | 快速回放回归、暂停/建造/单位移动控制测试 |
| `6666.zxsav` | **Survival** | 168485 | ~33.5k / 1020 | **推荐默认测试档**：中后期生存完整压力面（建筑/产兵/尸潮/多线） |
| `test.zxsav` | **Survival** | 443 | ~43k / 6 | 早期生存探索期 |
| `900survival.zxsav` | **Survival** | 11 | ~39.7k / 6 | 极早期开局状态 |

---

## 5. 常见排障

| 现象 | 处理方式 |
| :--- | :--- |
| MCP 报 `control server unavailable` | 确认游戏已通过 `run_game_with_control.ps1` 启动且 `TABCOOP_CONTROL_PORT=27901` 未被占用。 |
| `levelLoaded=false` | 仍在主菜单或加载中；使用 `-SavePath` 自动读档并等待加载完成。 |
| 操作后画面无变化 | 存档处于暂停态（先 `set_paused{paused:false}`），或建造坐标/资源不合法被游戏拒绝。 |
| 协议细节（ControlServer） | 仅监听 `127.0.0.1` 换行 JSON，单请求 10s 超时（示例：`{"id":1,"tool":"game_state","args":{}}`）。 |
| 等候大厅端到端测试（[`verify_lobby_flow.ps1`](../scripts/verify_lobby_flow.ps1)） | 双开无 `TABCOOP_TEST`（控制端口 `27901`/`27902`），验证 `open_lobby`、`lobby_state`、`lobby_action`（`probe`/`host_loopback`/`join_loopback`/`toggle_ready`/`open_new_survival`/`close_top_modal`/`load_save`/`return_to_menu`）、动态随机种子滚动、读档同步与方案 A 退回大厅全流程并保存截图。 |
| 生存新局平衡与全景图测试 | [`verify_new_survival_balance.ps1`](../scripts/verify_new_survival_balance.ps1) 验证大厅 `Co-op Balanced` / `Vanilla` 切换同步及开局实测一致；[`generate_panorama_comparisons.ps1`](../scripts/generate_panorama_comparisons.ps1) 调用 `ControlServer` 扩展指令 `start_survival`、`capture_panorama` 与 `map_metrics` 生成 4 场景同种子全景图及 `reports/panorama_metrics.json` / `reports/panorama_comparison.html`。 |
