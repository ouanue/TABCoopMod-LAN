# 04 — 网络通信协议与状态校验规范 (Network Protocol & State Checksum Specification)

> **状态**: 已与源码 [`INetworkTransport.cs`](../src/TABCoopMod/Networking/INetworkTransport.cs)、[`NetCommand.cs`](../src/TABCoopMod/Commands/NetCommand.cs) 与 [`StateHasher.cs`](../src/TABCoopMod/Determinism/StateHasher.cs) 对齐。

---

## 1. 传输层抽象接口 (`INetworkTransport`)

为了同时支持 **Steam P2P 正式联机** 与 **本机 TCP 环回自动化测试（Loopback + ChaosTransport）**，底层统一抽象为 `INetworkTransport`：

```csharp
namespace TABCoopMod.Networking
{
    public enum SendChannel : byte
    {
        ReliableOrdered = 0,     // 握手、快照分片、帧同步指令批次、状态哈希校验、TacticalPing
        UnreliableSequenced = 1  // 20Hz 盟友鼠标世界坐标、视野矩形框
    }

    public interface INetworkTransport : IDisposable
    {
        bool IsActive { get; }
        bool IsHost { get; }
        byte LocalPlayerId { get; } // 1 = Host, 2 = Client1, 3 = Client2
        IReadOnlyList<PeerInfo> ConnectedPeers { get; }

        event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        event Action<PeerInfo> OnPeerConnected;
        event Action<byte> OnPeerDisconnected;

        void StartHost(int maxPlayers = 3);
        void ConnectToHost(string targetAddressOrSteamId); // SteamId64 或 "127.0.0.1:27099"
        void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void SyncPeers(IReadOnlyList<PeerInfo> peers); // 房主全量同步成员与准备状态
        void PollEvents(); // 每渲染帧由主线程调用
    }
}
```

### 1.1 实现类说明
- **`SteamP2PTransport`**：复用内嵌 `Steamworks.NET` v10（`SteamMatchmaking` + `SteamNetworking`），`Channel 0` 走 `k_EP2PSendReliable`，`Channel 1` 走 `k_EP2PSendUnreliableNoDelay`，在主菜单/大厅与对局期间通过 `RegisterGlobalInviteListener()` + `PumpSteamCallbacksIfNeeded()` / `PollEvents()` 驱动 `SteamAPI.RunCallbacks()` 响应好友邀请与成员退房。
- **`LocalTcpLoopbackTransport`**：Host 监听 `127.0.0.1:27099`（TCP `NoDelay = true`），提供非侵入式 `ProbeLocalHost()`（在连接握手前用独立预握手状态返还 `LocalProbeResult`）与空闲断线检测（`IsSocketDisconnected`），用于双开与自动化回归。
- **`ChaosTransport`**：可配置的故障注入装饰器（延迟 / 抖动 / 丢包 / 断连窗口，带确定性 `seed`），包裹任意 `INetworkTransport`。

---

## 2. 二进制封包格式规范（Wire Framing）

所有消息采用小端序（Little-Endian）二进制编码（`BinaryWriter` / `BinaryReader`）：

### 2.1 通用包头（Packet Header — 8 Bytes）
```text
+-------------------+-------------------+-------------------+------------------------+
| Magic (2 Bytes)   | MsgType (1 Byte)  | SenderId (1 Byte) | PayloadLength (4 Bytes)|
| 0x54, 0x42 ("TB") | PacketType Enum   | 1..3              | Int32                  |
+-------------------+-------------------+-------------------+------------------------+
```
*(注：`LocalTcpLoopbackTransport.BuildFrame` 与 `ReadAvailableFrames` 实际均使用上述 8 字节头。)*

### 2.2 消息类型枚举 (`PacketType : byte`)

| 值 | 枚举名 | 通道 | 方向 | 说明 |
| :-: | :--- | :---: | :---: | :--- |
| `0x01` | `HandshakeRequest` | Reliable | C -> H | 客机请求加入，携带玩家名与 Mod 版本号 |
| `0x02` | `HandshakeAccept` | Reliable | H -> All | 房主分配 `PlayerId (1..3)` 并同步成员列表（含 `IsReady`） |
| `0x06` | `LobbyProbe` | TCP Pre-Handshake | C -> H | 客机探测本地主机是否在线（触发新连接预握手通道，不加入成员列表） |
| `0x07` | `LobbyProbeAck` | TCP Pre-Handshake | H -> C | 返回协议版本、在线玩家数、上限、当前 `LobbyRoomPhase` 与房主名 |
| `0x08` | `LobbyRosterSync` | Reliable | H -> All | 房主广播房间列表与每名成员 `IsReady` 状态，同时刷新当前 `LobbyRoomPhase` 与提示文案 |
| `0x09` | `ClientReadyState` | Reliable | C -> H | 客机切换准备 / 取消准备状态 |
| `0x0A` | `LobbyHostNotice` | Reliable | H -> All | 房主操作通知（选图中 / 加载中 / `ReturnToLobby` 召回客机退回主菜单重开大厅 / 房主退房） |
| `0x10` | `SnapshotBegin` | Reliable | H -> All | 宣告开始传输 `.zxsav` 快照（总字节数、分片数、`.zxcheck` 内容、初始随机种子、`SyncedEntityIdCounter` 等） |
| `0x11` | `SnapshotChunk` | Reliable | H -> All | 存档字节流分片（每片最大 `32,768` Bytes，按索引乱序缓冲组装） |
| `0x12` | `SnapshotLoadedAck` | Reliable | C -> H | 客机完成读档并上报初始状态就绪 |
| `0x13` | `SessionStart` | Reliable | H -> All | 全员就绪，对齐并进入 Lockstep 步进（携带起始 tick、seed、idCounter 与当前 `InputDelayTicks`） |
| `0x20` | `ClientInputReport` | Reliable | C -> H | 客机上报本人在当前 Turn 提交的 `NetCommand[]` 列表 |
| `0x21` | `TurnInputBroadcast` | Reliable | H -> All | 房主汇总全员在 `TargetSimTick` 的指令批次并广播（含空心跳包） |
| `0x22` | `HostPauseKeepalive` | Reliable | H -> All | 房主暂停或打开弹窗时周期保活心跳，区分于真实断线/失联 |
| `0x23` | `LevelEnded` | Reliable | H -> All | 房主权威同步关卡胜利/战败结算（`won`, `endTick`），客机立即弹出 `游戏结束` 并抑制暂停误报 |
| `0x30` | `StateChecksumReport` | Reliable | All -> H | 每 60 逻辑帧（2 秒）上报当前 `SimTick` 的 `StateHasher` 校验和 |
| `0x31` | `DesyncDetectedAlert` | Reliable | H -> All | 房主检测到状态不一致时下发告警并触发自动快照 `Auto-resync` |
| `0x40` | `AllyCursorAndCamera` | Unreliable | All <-> All | 20Hz 广播本地鼠标世界坐标、视野矩形与预览建筑状态 |
| `0x41` | `TacticalPing` | Reliable | All <-> All | 小地图/地面战术信号打点 |
| `0x42` | `RttPing` | Unreliable | H -> All | 局内 `500ms` 周期下发 `Stopwatch` 时间戳探测包，用于计算平滑 RTT |
| `0x43` | `RttPong` | Unreliable | C -> H | 客机原样回显 `RttPing`，供房主自适应调整 `InputDelayTicks (1..4)` |

---

## 3. 局内操作指令集定义 (`NetCommand`)

`NetCommand` 显式携带 `ExecutorEntityIds`，回放侧严禁读取本地 `CSelectable`：

```csharp
public enum NetCommandType : byte
{
    None = 0,
    ExecuteCommand = 1,      // 通用命令: 攻击、训练、研发、拆除、修理、市场、集结点、下车等
    GroupMoveOrPatrol = 2,   // 群体移动/巡逻/散开
    PlaceBuilding = 3,       // 放置建筑（含 EntityBonusCreation 市长奖励建筑）
    CancelCurrentOrder = 4,  // 取消当前执行命令/建造
    CancelQueuedOrder = 5,   // 取消生产队列槽位
    SetPaused = 6,           // 切换会话暂停/恢复状态
    SetGameSpeed = 7,        // 修改游戏倍速 (0..20)
    MayorVoteOrSelect = 8    // 市长选择（按 MayorId 确定性应用）
}
```

### 二进制序列化顺序（`NetCommand.WriteTo` / `ReadFrom`）
1. `byte Type`、`byte PlayerId`、`int TargetSimTick`、`bool IsShiftQueued`
2. `int CommandId`、`string BuildingTypeName`
3. `ushort ExecutorCount` + `ulong[ExecutorCount] ExecutorEntityIds`
4. `ulong TargetEntityId`、`PointF TargetPosition (float,float)`、`SizeF TargetSize (float,float)`、`PointF TargetOrigin (float,float)`
5. `Rectangle BuildingCellArea (4 × int)`、`bool IsBonusBuilding`、`bool InstantBuild`、`byte QueueSlotIndex`
6. `bool TargetPausedState`、`byte MayorOptionIndex`、`bool IsMayorFinalDecision`、`float GameSpeed`、`string MayorId`

---

## 4. 确定性状态哈希计算规范 (`StateHasher` v2)

`StateHasher.ComputeDigest()`（`HashVersion = 2`）每 60 个 `SimTick` 计算一次 **FNV-1a 64-bit** 全图摘要（使用 `object` + `GameBridge` 反射，顺序无关）：
1. **全局标量**：`HashVersion`、`SimTick`、`GameTimeMs`、五资源（`Gold`, `Wood`, `Stone`, `Iron`, `Oil`）以及反射扫描 `ZXLevelState` 全部 `Reserv/Worker/House/Food/Energy` 整数属性的 `ReservationChecksum`。
2. **实体遍历与过滤**：
   - 跳过 `Disposed` 实体；
   - 跳过高位本地 UI ID（`(entity.ID & 0x8000000000000000UL) != 0`）；
   - 跳过纯视觉/非交互实体：裸 `DXVision.DXEntity`（`entity.GetType() == typeof(DXEntity)`，如受击飙血/弹花）与环境飞鸟 `Raven`。
3. **分项累加（顺序无关）**：
   - **投射物**（`IsProjectileName`）：计入 `ProjectileCount` 与 `ProjectileChecksum`；
   - **常规单位/建筑**：坐标量化 `Math.Round(pos * 100)` 累加至 `PosChecksum`；`Life` 累加至 `LifeChecksum`；`CCommandable` 生产队列按 `(entity.ID, slot+1, commandId)` 累加至 `ProductionChecksum`。
4. 最终依次 Mix：`EntityCount`、`ProjectileCount`、`QueuedOrderCount`、`PosChecksum`、`LifeChecksum`、`ProjectileChecksum`、`ProductionChecksum`、`ReservationChecksum` 得到 `FullHash`。
