# 13 — 局域网（LAN）原生联机

> 面向对象：维护者、二次开发者。
> 面向玩家的操作说明另见游戏目录内的 `TABCoopMod_局域网联机说明.txt`。
> 本文记录改造动机、协议复用方式、逐处改动、取舍理由与已知限制。

---

## 1. 背景：上游只有两条链路

上游 v0.1.5 的会话传输由 `Sync/CoopSessionManager.CreateTransport` 决定，只有两个分支：

| 模式 | 传输实现 | 监听/连接 |
| --- | --- | --- |
| `Steam P2P` | `SteamP2PTransport` + `DirectFastPath` | Steam 大厅做信令，UDP 直连 |
| `LocalLoopback` | `LocalTcpLoopbackTransport` | **硬编码** `TcpListener(IPAddress.Loopback, 27099)` |

由此产生两个硬限制：

1. **Steam 路径必须登录 Steam**：`SteamP2PTransport.StartHost` 依赖 `SteamMatchmaking.CreateLobby`。离线、盗版、Steam 未登录、Steam 服务异常时直接 `k_EResultNoConnection`。
2. **回环路径无法跨机**：`INetworkTransport.cs` 里 `IPAddress.Loopback` 是写死的，套接字只在内核回环上，外部机器连不上。UI 也从不传真实 IP —— `CoopLobbyWindow.JoinLocalRoomClicked()` 固定 `JoinSession(useLoopback: true, "127.0.0.1")`。

于是"同一个路由器下两台机器联机"这件事，上游没有任何原生入口。

> `DirectFastPath` 的注释确实声称支持 LAN（`DirectCandidateKind.LanIPv4`），但那是 **Steam 大厅握手之后** 的传输加速层，不是独立链路 —— 没有 Steam 大厅就没有候选交换，`LanIPv4` 永远不会被用到。

---

## 2. 方案：第三条链路 `CoopTransportMode.Lan`

不新写一套协议，而是**让回环传输支持绑定非回环地址**，其余全部复用。理由：

- `LocalTcpLoopbackTransport` 已经是一套完整的、经过同机双开验证的 TCP 传输：握手、版本校验、玩家 ID 分配、断线检测、帧编解码（magic `0x54 0x42`）一应俱全。
- 它唯一的缺陷就是监听地址写死。改一个字段就能变成通用 TCP 传输。
- 复用意味着**同步层、UI 层、快照层零改动**，回归风险面最小。

改造后三种模式并存，互不干扰：

```csharp
public enum CoopTransportMode { Steam = 0, LocalLoopback = 1, Lan = 2 }
```

| 模式 | 绑定地址 | Chaos 包装 | 判定属性 |
| --- | --- | --- | --- |
| `Steam` | — | 仅 `TABCOOP_CHAOS` 时 | `!IsLanSession && !IsLocalLoopbackSession` |
| `LocalLoopback` | `IPAddress.Loopback` | 默认包装 | `IsLocalLoopbackSession` |
| `Lan` | `IPAddress.Any` → **自动升级为 `IPv6Any` 双栈** | **不包装** | `IsLanSession` |

---

## 2.5 为什么必须做 IPv6 双栈（v2 新增，决定性）

v1 只监听 IPv4（`new TcpListener(IPAddress.Any, 27099)`）。实测表明这在很多家庭宽带下**永远连不上，且与代码无关**：

| 事实 | 实测值 |
| --- | --- |
| 光猫 PPPoE WAN 地址 | `100.64.11.159`（`100.64.0.0/10`，RFC 6598 **运营商级 NAT**） |
| 实际公网 IPv4 出口 | `203.0.113.66`（`https://ipv4.icanhazip.com`） |
| 结论 | 出口 ≠ 光猫 WAN → **运营商侧还有一层 NAT，双层 CGNAT** |

双层 CGNAT 下 **IPv4 入站永远不可能**，在光猫上做端口映射也毫无意义（映射只到第一层 NAT）。但 IPv6 不受影响：

| 事实 | 实测值 |
| --- | --- |
| 本机全局 IPv6 | `240e:345:6789:abcd:1234:5678:9abc:def0` |
| 出站验证 | `curl -6 https://api64.ipify.org` 回显同一地址 |
| 入站验证 | 双栈监听后自连 `[240e:…:583f]:27099` → `Established` |

因此 v2 把 `Lan` 模式的监听改成**双栈**：

```csharp
var v6 = new TcpListener(IPAddress.IPv6Any, _port);
v6.Server.DualMode = true;
v6.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
v6.Start();
```

`DualMode = true` + `IPv6Only = false` 让同一个端口同时接受 IPv4 与 IPv6 客户端，`LocalLoopback` 模式完全不受影响（它仍走 `IPAddress.Loopback` 分支）。

配套改动：

1. `ConnectToHost` 的 `TcpClient` **必须按目标地址族创建** —— 默认 `new TcpClient()` 是 IPv4 的，连 IPv6 字面量会抛 `发现的地址或指定的地址与套接字地址族都不匹配`（这是 v2 实测踩到的真实 bug）。
   ```csharp
   _clientToHost = IsIPv6Literal(host)
       ? new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true }
       : new TcpClient { NoDelay = true };
   ```
2. 端点解析改用 `ParseEndpoint`：`Split(':')` 会把 IPv6 切碎。支持 `1.2.3.4`、`1.2.3.4:27099`、裸 `240e:345::1`、`[240e:345::1]:27099`。
3. `GetGlobalIPv6AddressInfos()` 过滤链路本地 `fe80::/10`、`fec0::/10`、回环、IPv4-mapped 与 ULA `fc00::/7`（ULA 只在同一组织内有效），只保留真正可跨公网的地址。
4. `ProbeLanHost` 对 `fe80:` 开头直接给出"链路本地地址跨网络无效"的明确原因，而不是笼统的超时。
5. 大厅地址面板把 IPv6 **排在 IPv4 之前**（CGNAT 下 IPv4 对异地队友无用），并提示"异地联机把 IPv6 告诉队友"。

---

## 3. 逐处改动

### 3.1 `Networking/INetworkTransport.cs`

新增字段与构造：

```csharp
private readonly IPAddress _bindAddress;
private readonly int _port;
private bool _dualStack;
private string _lastRemoteEndpoint = "127.0.0.1";

public LocalTcpLoopbackTransport() : this(IPAddress.Loopback, DefaultPort) { }

public LocalTcpLoopbackTransport(IPAddress bindAddress, int port)
{
    _bindAddress = bindAddress ?? IPAddress.Loopback;
    _port = port > 0 && port <= 65535 ? port : DefaultPort;
}

public bool IsLanBound => _bindAddress != null && !IPAddress.IsLoopback(_bindAddress);
public int Port => _port;
public IPAddress BindAddress => _bindAddress;
```

`StartHost` 原本硬编码回环：

```csharp
_listener = new TcpListener(IPAddress.Loopback, DefaultPort);
```

改为**双栈监听**（`_bindAddress` 是 `IPAddress.Any` 时升级为 IPv6Any，见 §2.5）：

```csharp
if (IPAddress.Any.Equals(_bindAddress))
{
    try
    {
        var v6 = new TcpListener(IPAddress.IPv6Any, _port);
        v6.Server.DualMode = true;
        v6.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        v6.Start();
        _listener = v6;
        _dualStack = true;
    }
    catch (Exception ex)
    {
        ModLogger.Warn($"[LocalTcpLoopbackTransport] Dual-stack listen failed ({ex.Message}); falling back to IPv4-only.");
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
    }
}
else
{
    _listener = new TcpListener(_bindAddress, _port);
    _listener.Start();
}
```

**地址排序**（虚拟网卡不能排在物理网卡前面，否则大厅把 Radmin/ZeroTier 地址当成"本机 IP"发给队友）：

```csharp
public sealed class LocalAddressInfo
{
    public string Address;
    public string AdapterName;
    /// 0 = physical LAN/Wi-Fi, 1 = virtual adapter, 2 = APIPA / link-local
    public int Rank;
    public bool IsPhysical => Rank == 0;
    public string Display => ...;   // "192.168.1.100 (WLAN)" / "虚拟网卡/Radmin VPN" / "无效地址"
}

private static readonly string[] VirtualAdapterMarkers =
{
    "radmin", "zerotier", "tailscale", "hamachi", "wg", "wireguard", "openvpn", "tap-",
    "tap ", "tun", "vmware", "virtualbox", "vbox", "hyper-v", "vethernet", "loopback",
    "npcap", "wintun", "softether", "pgyvpn", "pugongying", "蒲公英", "parallels"
};

public static List<LocalAddressInfo> GetLocalAddressInfos();   // rank 0 → 2 稳定排序
public static List<string> GetLocalIPv4Addresses();            // 兼容旧签名
public static string GetPreferredLanAddress();
```

**IPv6 枚举**只保留真正可跨公网的地址（过滤 `fe80::/10`、`fec0::/10`、回环、IPv4-mapped、ULA `fc00::/7`）：

```csharp
public sealed class LocalV6AddressInfo { public string Address; public string AdapterName; public int PrefixLength; ... }
private static bool IsGlobalIPv6(IPAddress addr);
public static List<LocalV6AddressInfo> GetGlobalIPv6AddressInfos();
public static string GetPreferredGlobalIPv6();
public static bool IsIPv6Literal(string text);
```

**端点解析**（`Split(':')` 会把 IPv6 切碎，必须专用解析器）：

```csharp
/// 支持 "1.2.3.4"、"1.2.3.4:27099"、裸 "240e:345::1"、"240e:345::1:27099"、"[240e:345::1]:27099"
public static void ParseEndpoint(string input, int defaultPort, out string host, out int port);
```

`ConnectToHost` 三处改动 —— 端点解析、**地址族匹配**（默认 `new TcpClient()` 是 IPv4，连 IPv6 会抛"地址族不匹配"）、有界连接：

```csharp
ParseEndpoint(targetAddressOrSteamId, _port, out host, out port);

_lastRemoteEndpoint = IsIPv6Literal(host) ? $"[{host}]:{port}" : $"{host}:{port}";
_clientToHost = IsIPv6Literal(host)
    ? new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true }
    : new TcpClient { NoDelay = true };

IAsyncResult ar = _clientToHost.BeginConnect(host, port, null, null);
if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))   // 5000 ms
{
    throw new TimeoutException($"连接 {_lastRemoteEndpoint} 超时（{ConnectTimeoutMs} ms 无响应）。…");
}
```

**防火墙自动放行**（Windows 默认丢弃入站 TCP，对客机来说和"没人监听"完全一样）：

```csharp
public enum FirewallOpenResult { AlreadyPresent, Created, Failed, NotAttempted }
public static string FirewallStatus { get; }             // 面板显示用
public static string BuildFirewallCommand(int port);
public static bool FirewallRuleExists(int port);
public static FirewallOpenResult EnsureFirewallOpen(int port);          // 非提权尝试
public static bool EnsureFirewallOpenElevated(int port);                // UAC 提权
```

**带原因的探测**（上游 `ProbeLocalHost` 把所有失败都吞成 `IsOnline = false`，无法远程排障）：

```csharp
public static bool ProbeLanHost(string host, int port, int timeoutMs,
                                out LocalProbeResult result, out string failure);
```

区分：未填地址 / DNS 失败 / APIPA 无效地址 / **fe80:: 链路本地无效** / 超时 / 连接被拒 / 网络不可达 / 连上但无回包 / 包类型不对。

诊断字符串按绑定情况切换，避免在 LAN 会话里显示误导性的 `Local Loopback (127.0.0.1)`：

```csharp
public string ActivePathSummary => IsLanBound
    ? (IsHost ? $"LAN ({_bindAddress}:{_port})" : $"LAN ({_lastRemoteEndpoint})")
    : "Local Loopback (127.0.0.1)";
```

### 3.2 `Sync/CoopSessionManager.cs`

`CreateTransport` 重写为按模式分派：

```csharp
private static INetworkTransport CreateTransport(CoopTransportMode mode, int port)
{
    INetworkTransport transport;
    switch (mode)
    {
        case CoopTransportMode.LocalLoopback:
            transport = new LocalTcpLoopbackTransport(IPAddress.Loopback, port); break;
        case CoopTransportMode.Lan:
            transport = new LocalTcpLoopbackTransport(IPAddress.Any, port); break;
        default:
            transport = new SteamP2PTransport(); break;
    }

    if (TestHarnessConfig.HasChaos && mode != CoopTransportMode.Lan)
        transport = new ChaosTransport(transport, BuildLocalChaosConfig());
    else if (mode == CoopTransportMode.LocalLoopback)
        transport = new ChaosTransport(transport, BuildLocalChaosConfig());

    return transport;
}
```

**为什么 LAN 刻意不包 `ChaosTransport`** —— 两条独立理由，缺一不可：

1. 局域网链路的卖点就是低延迟；默认 `80ms 延迟 + 20ms 抖动 + 2% 丢包` 会把它毁掉，玩家第一反应会以为"局域网比 Steam 还卡"。
2. 协议层面：`BroadcastLobbyRoster` 在 `IsLocalLoopbackSession && !TestHarnessConfig.HasChaos` 时**额外追加 4 个 chaos 字段（共 13 字节）**，接收端 `:2263` 对称解析。如果 LAN 复用了回环判定，两端会因为多/少 13 字节而错位。

判定拆分是这次改造的关键一行：

```csharp
public static bool IsLanSession
    => UnwrapTransport() is LocalTcpLoopbackTransport lan && lan.IsLanBound;

public static bool IsLocalLoopbackSession
    => UnwrapTransport() is LocalTcpLoopbackTransport lb && !lb.IsLanBound;
```

上游原本是 `UnwrapTransport() is LocalTcpLoopbackTransport` —— 若不拆，LAN 会话会被误判为回环会话，连带触发 chaos 面板可见、roster 多写 13 字节。

新增对外入口（旧签名保留为薄转发，兼容 `ControlServer` / `TestingLifecyclePatches` 等既有调用方）：

```csharp
public static void StartHostSession(bool useLoopback);              // 旧，转发
public static void StartHostSession(CoopTransportMode mode, int port = DefaultPort);
public static void StartHostSessionLan(int port = LocalTcpLoopbackTransport.DefaultPort);

public static void JoinSession(bool useLoopback, string target);    // 旧，转发
public static void JoinSession(CoopTransportMode mode, string target);
public static void JoinSessionLan(string hostOrEndpoint);
```

`StartHostSessionLan` 会打印可直接转给队友的地址：

```
[CoopSessionManager] LAN host ready. Share this address with clients: 192.168.1.100:27099
```

### 3.3 `Core/GameBridge.cs` — 借用引擎自带输入框

原本担心"要自己实现可编辑文本框"（按键边沿捕获、光标、退格、焦点、防按键穿透…）。实际发现引擎 `ZXMessageBoxHelper` 自带 `ShowInputBox`，且 mod 已在 `ObfuscatedSymbols.cs:181` 登记了它的混淆名 `#=znd_PcbSNuH79` —— 只是从未被调用。

反射签名（已用反射实测确认）：

```
static ZX.GUI.ZXWindow ShowInputBox(
    string title, string message, string initialValue,
    DXVision.DXEditBoxObject+TypeOfInput inputType, int maxLength,
    Action<string> onOk, Action onCancel, Func<string,bool> validator)
```

`TypeOfInput` 是 `nestedPublic` 枚举，可直接在 C# 里写，无需反射构造枚举值。

绑定以**可选**方式注册 —— 不加入 `Check()`，因为 `Check` 会把 `missingCount` 累加进绑定校验，导致整个 mod 判定失败：

```csharp
_typeEditBoxTypeOfInput = typeof(DXEditBoxObject).GetNestedType("TypeOfInput");
_methodShowInputBox = AccessTools.Method(TypeZXMessageBoxHelper,
    ObfuscatedSymbols.Method_MsgBox_ShowInputBox,
    new[] { typeof(string), typeof(string), typeof(string), _typeEditBoxTypeOfInput,
            typeof(int), typeof(Action<string>), typeof(Action), typeof(Func<string,bool>) });
// 只打日志，不 Check
```

对外 API 在绑定缺失时自动降级为 `ShowAlertMessageBox`，不会崩溃：

```csharp
public static bool HasInputBoxSupport;
public static object ShowInputBoxMessageBox(string title, string message, string initialValue,
    int maxLength, Action<string> onOk, Action onCancel = null,
    Func<string,bool> validator = null, string inputTypeName = "AllCharactersExceptNewLines");
```

### 3.4 `UI/CoopLobbyWindow.cs`

新增控件全部插在既有布局变量（`leftW` / `rightX` / `rightW` / `bodyH` / `u`）的剩余空间里，未改动任何原有控件坐标：

| 位置 | 控件 | 行为 |
| --- | --- | --- |
| 左列 `localDesc` 之后 | `_btnCreateLan`【创建局域网房间】 | `StartHostSessionLan()`，成功后 `PromptForFirewallIfNeeded()` |
| 左列 `_btnCreateLan` 下方 | `_lanAddressText` 蓝色方块 | `BuildLanAddressHint()` 实时显示本机 IP 与防火墙状态 |
| 右列 `_btnJoinLoopback` 之后 | `_btnJoinLan`【加入局域网房间…】 | `JoinLanRoomClicked()` 弹输入框 |
| 右列 `_btnJoinLan` 之后 | `_btnProbeLan`【测试局域网房主连通性】 | `ProbeLanHostClicked()` 只发探测包，不进房 |

**地址排序（v2 修复）**。`GetLocalAddressInfos()` 把地址分三档并稳定排序，替代原来"按网卡枚举顺序取第一个"的做法 —— 后者会把 Radmin VPN 的 `26.x.x.x` 排到真实 WLAN 前面，把错误 IP 交给队友：

| Rank | 含义 | 判定 |
| --- | --- | --- |
| 0 | 物理网卡 | 名称/描述不含虚拟网卡标记，且不是 `NetworkInterfaceType.Tunnel` |
| 1 | 虚拟网卡 | 命中 `VirtualAdapterMarkers`（radmin / zerotier / tailscale / wireguard / tap / tun / vmware / hyper-v / 蒲公英 …） |
| 2 | 无效地址 | `169.254.*` APIPA |

**防火墙自动放行**。Windows 默认丢弃入站 TCP，对客机而言与"房主没开房"完全无法区分。主机点【创建局域网房间】后：

1. 后台 `EnsureFirewallOpen()` 先 `netsh ... show rule`，不存在则 `netsh ... add rule`（非提权，通常失败）；
2. 失败则弹 `ShowYesNoMessageBox` 询问是否放行，选"是"走 `EnsureFirewallOpenElevated()`：`ProcessStartInfo{ UseShellExecute = true, Verb = "runas" }` 触发 UAC，用户拒绝时捕获 `Win32Exception`（`ERROR_CANCELLED`）并降级为提示手动命令；
3. `_lanAddressText` 始终显示 `防火墙: ...` 实际状态，失败时附完整 `netsh` 命令。

**连通性探测**。`ProbeLanHost()` 不吞异常，而是把失败原因分类回报（这是 v2 最重要的可诊断性改进）：

| 情况 | 回报 |
| --- | --- |
| 空输入 | `未填写 IP 地址` |
| DNS 失败 | `无法解析主机名: ...` |
| `169.254.*` | `169.254.x.x 是无效地址（对方网卡没拿到 IP）` |
| **`fe80:*`** | **`fe80:: 是链路本地地址，只在同一台交换机内有效，跨网络无法连接`** |
| 连接超时 | IPv6 目标：`连接超时（n ms 内没有任何响应）。检查：① 房主是否点了【创建局域网房间】② 房主的 Windows 防火墙是否放行了 TCP 27099（IPv6 也需要放行）③ IPv6 地址是否抄错`；IPv4 目标另加 `④ 双方是否在不同网络（异地联机请用 IPv6 地址）` |
| `ConnectionRefused` | `连接被拒绝（对方主机在线但 27099 端口没有监听）` |
| `HostUnreachable` | `网络不可达（该 IP 与本机不在同一网络）` |
| 连上无回包 | `TCP 已连上，但对方没有回应探测包（端口被别的程序占用）` |
| 包类型不符 | `对方端口有响应，但不是本 mod 的房主（返回包类型={pktType}）` |

探测客户端也按目标地址族创建，否则 IPv6 目标会抛"地址族不匹配"：

```csharp
private static TcpClient CreateProbeClient(string host, int timeoutMs)
{
    IPAddress parsed;
    bool isV6 = IPAddress.TryParse(host, out parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6;
    var client = isV6 ? new TcpClient(AddressFamily.InterNetworkV6) : new TcpClient();
    client.NoDelay = true;
    client.ReceiveTimeout = timeoutMs;
    client.SendTimeout = timeoutMs;
    return client;
}
```

`BuildLanAddressHint()` 输出形如（**IPv6 排在 IPv4 之前**，因为 CGNAT 下 IPv4 对异地队友无用）：

```
房间监听 27099 端口（IPv4 + IPv6 双栈）
异地联机把 IPv6 告诉队友: 240e:345:6789:abcd:1234:5678:9abc:def0
同一局域网用 IPv4: 192.168.1.100
虚拟网卡(VPN): 虚拟网卡/Radmin VPN (26.1.2.3)
防火墙: 未检测
```

`JoinLanRoomClicked()` 预填**物理网卡**同网段前缀（`GetPreferredLanAddress()`，不再取 `localIps[0]`），减少手输：

```csharp
GameBridge.ShowInputBoxMessageBox(
    "加入局域网房间",
    "请输入房主的 IP（默认端口 27099）\n" +
    "同一局域网: 192.168.1.100 或 192.168.1.100:27099\n" +
    "异地联机: 240e:345:6789:abcd:1234:5678:9abc:def0\n" +
    "         （IPv6 带端口要加方括号 [240e::1]:27099）\n" +
    "注意：169.254.x.x 和 fe80:: 都不是可用地址。",
    suggested, 64, onOk, onCancel, validator, "AllCharactersExceptNewLines");
```

输入框的 `onOk` 在连接前做两级拦截：`169.254.` 开头 → 提示无效地址；`fe80:` 开头 → 提示链路本地地址跨网络无效。`ProbeLanHostAsync()` 也改用 `ParseEndpoint()`，`[240e::1]:27099` 不再被 `Split(':')` 切碎。

`ConnectToHost` 改为有界连接（`BeginConnect` + `WaitOne(ConnectTimeoutMs)`，5 秒），超时抛带排障提示的 `TimeoutException`，而不是让 UI 无限期挂住。

`BuildIdentityLine` 增加模式显示，玩家能一眼看出当前走哪条链路：

```csharp
string mode = CoopSessionManager.IsLanSession ? "局域网"
            : (CoopSessionManager.IsLocalLoopbackSession ? "本地回环" : "Steam P2P");
```

### 3.5 测试钩子（自动化联调）

| 环境变量 | 作用 |
| --- | --- |
| `TABCOOP_LAN=1` | 自动起/加入的会话使用 LAN 传输 |
| `TABCOOP_LAN_HOST=ip[:port]` | 客机目标地址 |

`ControlServer` 的 `lobby_state` 增加 `isLanSession` / `localIps` 字段；`lobby_action` 增加 `host_lan` / `join_lan` / `join_lan_direct` / `set_lan_target`。

> `ControlServer` 只监听 `IPAddress.Loopback`，所以自动化钩子只能本机驱动，不能跨机。

### 3.6 `Core/ModInfo.cs` — 版本号策略

```csharp
public const string Version = "0.1.5-LAN";
public const string AssemblyVersion = "0.1.5.0";   // 故意不变
```

`AssemblyVersion` 保持 `0.1.5.0` 是**刻意**的：游戏根目录的 `TheyAreBillions.exe.config` 里，`appDomainManagerAssembly` 钉死了 `TABCoopMod, Version=0.1.5.0`。改程序集版本就得连带改游戏 config，而 config 是注入入口，动它风险大且会影响卸载还原。

`Version`（协议版本，握手里传输）改成 `0.1.5-LAN`，效果是本 LAN 版与上游 v0.1.5 混装时，握手会**显式**触发 `OnVersionMismatch` 弹窗报"版本不匹配"，而不是静默走错协议导致更难看懂的卡死或错位。

---

## 4. 构建

### 4.1 环境准备

上游 `TABCoopMod.csproj` 引用 `..\..\lib\*.dll`（相对 csproj 上两级 = 仓库根 `lib/`），这些 DLL 由 `scripts/extract_sdk.ps1` 从游戏 exe 内存解出，不进版本库。

两个必踩的坑：

1. **`extract_sdk.ps1` 必须用 Windows PowerShell 5.1 跑**。用 PowerShell 7 会报 `Exception of type 'System.Exception' was thrown.` —— .NET Core 无法反射 .NET Framework 混淆程序集。
2. **`lib/` 拷到仓库根**。若只放在外层目录，构建会出 324 个 `未能找到类型或命名空间名"DXVision"` 之类的误导性错误，加 `-v normal` 才能看到真因 `MSB3245: ... 但它不存在`。

### 4.2 目标框架参考程序集

原 csproj 依赖本机安装的 .NET Framework Reference Assemblies。若本机缺失（本次环境即如此），补一条 PackageReference：

```xml
<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

### 4.3 构建脚本

`scripts/build_lan.ps1` 封装了环境变量重定向（`TEMP` / `TMP` / `APPDATA` / `LOCALAPPDATA` / `DOTNET_CLI_HOME` / `NUGET_PACKAGES`）—— 当用户目录不可写时，`dotnet` CLI 会在 `NuGet.Config`、`~/.dotnet`、临时目录上连报三次 `UnauthorizedAccessException`。

产物：`src/TABCoopMod/bin/Release/TABCoopMod.dll`。

---

## 5. 验证

### 5.1 已验证（实机）

| 项 | 证据 |
| --- | --- |
| 绑定非回环 | `netstat` → `0.0.0.0:27099 Listen`；日志 `Host listening on 0.0.0.0:27099 (Lan=True)` |
| 跨网卡可达 | `Test-NetConnection 192.168.1.100:27099` = True；虚拟网卡 `26.x.x.x:27099` = True |
| 跨进程握手 | 客机日志 `HandshakeAccepted! LocalPlayerId=2, Peers=2, HostVer=0.1.5-LAN` |
| 双端房间状态 | 两端 `peers` 均含 2 人，客机 `ready=true`，房主 `canStart=true` |
| UI 显示 | 客机 `角色: 客机 P2 \| 链路: LAN (192.168.1.100:27099) \| RTT: 22ms` |
| 纯手动入口 | 不借助任何钩子，点【创建局域网房间】即监听 `0.0.0.0:27099` |
| 引擎输入框 | 点【加入局域网房间…】正常弹出，预填网段前缀，可编辑可确认 |
| 绑定完整性 | `ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing)`，`Classes OK=64, failed-tolerant=0` |
| 构建 | 0 warning / 0 error |
| **地址排序**（v2） | 日志 `LAN host ready. Share this address with clients: 192.168.1.100, 26.1.2.3, 169.254.1.1, 169.254.2.2:27099` — 物理 WLAN 排首位，虚拟网卡与 APIPA 在后 |
| **连通性探测**（v2） | `probe_lan 192.168.1.100:27099` → `✅ 可达 — 房主: HostPlayer，人数 1/3，等候大厅中` |
| **失败诊断**（v2） | `probe_lan 192.168.1.3:27099` → `❌ 无响应 — 连接超时（1200 ms 内没有任何响应）…` |
| **IPv6 双栈监听**（v2） | 日志 `Host listening on 0.0.0.0:27099 (Lan=True, DualStack=True)`；`Get-NetTCPConnection` → `LocalAddress = ::`（IPv6Any） |
| **IPv6 地址枚举**（v2） | 日志 `LAN host ready (dual-stack). IPv4: 192.168.1.100, … :27099 \| IPv6: 240e:345:6789:abcd:1111:2222:3333:4444, 240e:345:6789:abcd:1234:5678:9abc:def0` |
| **IPv6 入站可达**（v2） | 自连全局地址 → `IPv6 connect OK`；`Test-NetConnection 240e:…:583f -Port 27099` = `True` |
| **IPv6 端到端进房**（v2） | 客机 `TABCOOP_LAN_HOST=[240e:345:…:583f]:27099` → `sessionActive=true, localPlayerId=2, peers=2, RTT≈266ms`；房主侧 `Get-NetTCPConnection` → `240e:…:583f:…:583f Established` |
| **IPv6 探测**（v2） | `probe_lan [240e:345:…:583f]` → `✅ [240e:…]:27099 可达 — 房主: HostPlayer，人数 1/3，等候大厅中` |
| **地址族修复**（v2） | 修复前客机报 `发现的地址或指定的地址与套接字地址族都不匹配`；`TcpClient(AddressFamily.InterNetworkV6)` 后连接成功 |

### 5.2 真实双机验证（经 Radmin VPN 虚拟局域网）

两台物理机（异地，均无公网 IPv6，IPv4 均为双层 CGNAT）通过 Radmin VPN 建立点对点虚拟局域网后实测：

| 项 | 证据 |
| --- | --- |
| 链路打通 | 朋友机 Radmin 地址 `26.124.95.106` 的 `445/135/3389` 均 OPEN（27~42ms，点对点直连）；本机 `26.178.89.181` |
| 房主监听 | 本机点【创建局域网房间】→ `::` 27099 双栈监听，`sessionActive=true, isLanSession=true, roomPhase=InLobby` |
| 跨机进房 | 朋友机点【加入局域网房间…】输入 `26.178.89.181` → 房主 `lobby_state`：`peers=[HostPlayer(1,ready), 1#2(2,ready=true)]`，`smoothedRttMs=36.43`，`canStart=true` |
| **局内同步** | 真实开局后，单位移动/建筑/波次在两台真机间同步正常（用户实战确认） |

### 5.3 仍属环境限制（非 mod 缺陷）

1. **公网 IPv6 双向缺失时**：IPv6 直连要求**双方**都有全局 IPv6。实测一方无公网 IPv6（只有 `fe80::`/`fd::`）时 IPv6 路线不可用，只能走虚拟局域网（Radmin / ZeroTier / Tailscale）或 TCP 隧道。
2. **UAC 提权路径**。`EnsureFirewallOpenElevated()` 的 UAC 弹窗在自动化环境里无法点按，只验证到非提权路径失败并正确降级为提示手动命令；真实使用中玩家手动点一次"是"即可永久放行。

---

## 5.5 双机连不上时的排查顺序

局域网模式的"连不上"绝大多数不是 mod 的问题。按下面顺序查，每一步都能得到确定答案：

0. **先判断你们是不是同一个网络**。`tracert -d <对方IP>`：第一跳就是对方 → 同一局域网，填 IPv4 即可。第一跳是网关、第二跳才到对方 → 中间隔着 NAT，见第 1 步。**如果对方是异地朋友（各自在不同城市的宽带），"局域网"根本不成立，请直接跳到第 7 步用 IPv6。**
1. **先弄清谁该当房主**。执行 `tracert -d <对方IP>`：如果第一跳就是自己的网关、第二跳才是对方，说明两台机器中间隔着路由器 NAT —— **只能由外层（离公网更近）那台当房主，内层那台出站连接**。内层当房主时，外层永远连不进来（除非做端口映射）。
2. **确认 IP 类型**。房主在自己的大厅左栏看到 `同一局域网用 IPv4: 192.168.x.x` 才是可用地址。`169.254.*` 是网卡没拿到 DHCP，无效；`26.*` / `10.*` 常是 Radmin/ZeroTier 虚拟网卡，只在双方都装了同一 VPN 时才通。
3. **用【测试房主连通性】**。这一步只发一个探测包，不占端口也不建房。三条典型输出：

   | 输出 | 含义 | 下一步 |
   | --- | --- | --- |
   | `✅ ... 可达` | 网络与端口都通 | 直接点【加入局域网房间…】 |
   | `❌ 连接超时` | 包被丢弃 | 房主放行防火墙（见下）或 IP 填错 |
   | `❌ 连接被拒绝` | 主机在线但没人监听 | 房主还没点【创建局域网房间】 |

4. **房主放行防火墙**。点【创建局域网房间】后 mod 会自动尝试，失败会弹 UAC 询问。若当时选了"否"，以**管理员**身份运行：

   ```powershell
   netsh advfirewall firewall add rule name="TABCoopMod LAN 27099" dir=in action=allow protocol=TCP localport=27099
   ```

   放行后大厅左栏会显示 `防火墙: 已放行`。**IPv6 入站同样受 Windows 防火墙管辖，也必须放行。**

5. **`ping` 不通不代表网络不通**。Windows 默认丢弃 ICMP，所以 `ping` 超时完全正常；判断依据永远是 TCP（第 3 步的探测，或 `Test-NetConnection <IP> -Port 27099`）。

6. **实在不在同一网络**就用虚拟局域网兜底：双方装同一个 ZeroTier / Tailscale / Radmin 网络，然后填虚拟网卡地址。此时两端都在同一虚拟的二层网络里，防火墙仍是唯一门槛。

7. **异地联机优先用 IPv6**（v2 新增）。先检查有没有公网 IPv6：

   ```powershell
   Get-NetIPAddress -AddressFamily IPv6 | Where-Object { $_.IPAddress -notlike 'fe80*' -and $_.IPAddress -notlike 'fd*' }
   ```

   `240e:` / `2408:` / `2409:` 等以 `2` 开头的就是公网地址。房主大厅左栏会直接显示 `异地联机把 IPv6 告诉队友: 240e:…`，队友把这一整串粘进【加入局域网房间…】即可（带端口要写成 `[240e::1]:27099`）。

   > **为什么不能靠 IPv4**：很多家庭宽带是双层 CGNAT（光猫 WAN 是 `100.64/10`，运营商侧还做了一层 NAT），IPv4 入站永远不可达，在光猫上做端口映射也没用。判断方法：`curl https://ipv4.icanhazip.com` 的返回值与光猫 WAN 口地址不同 → 就是双层 CGNAT。此情况下只有 IPv6 可行。

   若完全没有公网 IPv6（`Get-NetIPAddress` 里只有 `fe80::`），就只能回到第 6 步用虚拟局域网。

---

## 6. 已知限制

- **两台版本必须一致**（都是 `v0.1.5-LAN`）。混装上游客机，握手报版本不匹配。
- **联机失步自愈（v6，引擎并行循环序列化）**：真正根因不在 mod 的异步 tick，而在引擎 `DXVision.dll` 的 `DXHelper_Task.For`——它把循环体按 `minIterationsForConcurrency`/`maxCores` 切块后多线程跑，于是共享累加器、broadphase 插入、arbiter 候选的**访问顺序取决于核数与线程调度**，两台机器（甚至同机两次运行）会得到不同的浮点舍入，逐 tick 累积成位置漂移，攒到 `MissingOnClient≥10` 连续 4 次就触发 `Auto-resync` 快照重建（即用户看到的"每 1~3 分钟断线重连"，日志里 780~860KB 快照、8~11 秒卡顿）。**v6 修复**：Harmony 前缀替换 `DXHelper_Task.For` 的三个重载，在确定性沙箱内改为串行 `for` 循环；物理两段循环（`StartUpdatePhysics` 与 `OnNewPositionComputed` 回读）与**逻辑 tick 本身**都串行化。A/A 对照（同一二进制、同一存档 `PVE2.zxsav`、同一种子 `20261109`、零输入）此前约一半的运行会漂移，修复后连续多轮全绿。代价：物理步进变慢（17k 实体存档 `physFor1` ≈ 6~9ms/t，帧率约 12~17fps），正确性优先。想要帧率可以把逻辑 tick 的串行关掉：`TABCOOP_SERIALIZE_LOGIC=0`（物理串行不可关，否则必然漂移）。另有 `TABCOOP_PROJ_DUMP=1` 会按 epoch 打印 `[ProjDump] Tick=… role=… count=… ids=…`，用于对比两端抛射物集合。
- **异步 tick 重叠默认关闭（v6.1）**：`LockstepClock.cs` 的 `canOverlapAsync` 改为 opt-in，只有 `TABCOOP_ENABLE_ASYNC_TICK=1` 才开。重叠时逻辑 tick 跑在 `_TaskLogic` 续体线程上（引擎自己的 `UpdateGameLogic` body），与主线程的渲染/DXScene pass 交错，实体位置被采样的时刻就由**每台机器的线程调度**决定 —— 实时联机里表现为每段开始 60~200 tick 内出现亚像素位置漂移，随后 `MissingOnClient` 攒够就 1~4 分钟重建一次快照。A/A 测试一直靠 harness 设 `TABCOOP_DISABLE_ASYNC_TICK=1` 关着它，所以此前的连绿不能证明"开着重叠也是确定的"；而发布版恰恰跑在默认开启上。同步 tick 与回放路径一致，代价是后期存档吞吐压在 12~30fps。
- **`phys=` 物理步数对照**：每个 epoch 的摘要末尾带 `phys=<DXPhysicWorld.NUpdates>`，两端同 tick 必须相同；不一致时在 `MATCH` 行后追加 `PHYS-UPDATES-MISMATCH host=… client=… delta=…`。`phys=` **不进** `HardHash`/`FullHash`；v6.2 起它随 `StateChecksumReport` 的 wire 末尾追加（仍是 append-only，旧构建对端照常解析），所以 MATCH 行不再出现假的 `client=0`。
- **共享物理时钟原点（v6.2）**：真实双机联机的失步根因已定位到**物理时钟基线两端不对称**。引擎的 `DXGame.UpdatePhysics` 追赶循环是 `while (game.GameTime - DXPhysicWorld.LastGameTimeUpdatePhysics >= 83.333…) { LastGameTimeUpdatePhysics += 83.333…; UpdateDefaultTime(); }`，步长是编译期常量，**步数只由这两个 double 决定**。此前两端各自用本地 `game.GameTime` 落基线（`GameBridge.AlignSimulationClocks`），差 83.33ms，于是**每 reload 一次 client 就永久比 host 多跑 1 个物理步**（实测一场联机累积到 18 步，`dPhys` 段内恒定、跨 reload +1）；物理步数不同 → 同一 tick 的实体位置从**第一个 epoch** 就不同（`Pos=` 校验和 841 对里 100% 不同）→ 逐 tick 累积 → `MissingOnClient` 攒够触发快照重建。
  修复：房主在 `SnapshotBegin`（append-only 字段）里把自己的物理时钟原点一起下发，客机原样采用；`ApplyDeterministicLevelBaseline` 优先用共享原点对齐，未知时才回退到本地 `GameTime`。新增 `GameBridge.AlignSimulationClocksTo(originMs)`、`LockstepClock.SharedClockOriginMs`、`LockstepClock.ApplySharedClockOrigin(originMs)`。
  另外 `RequestPostLoadCalibration` / `ResetSyncTrackingState` / `OnReturningToMainMenu` 三处把 `LastGameTimeUpdatePhysics` 置 0 后原本无人重新对齐（上游 v0.1.9 同样如此），这也是必须一起处理的原因。
- **移植上游 v0.1.9 的确定性修复（v6.2）**：
  - **忽略表现层市民 Colonist**（`GameBridge.IsColonistType`，`StateHasher` 标 `FlagIgnore`）：`ZX.Entities.Colonist`（`Worker_A`/`Worker_B`）只挂 `CSalivable`、无血条、不参战也不挡建造，只在基地建筑间随机散步，**但此前被计入状态校验**，会凭空制造位置漂移并波动 `EntityCount`。同时 `VisualVfxIsolationPatch` 增加 `Building.SpawnColonists`，把这些市民隔离到本地视觉 ID 轨道，不消耗同步 RNG。
  - **`StructureNearEnemiesReplayPatch`**：回放已提交的 `PlaceBuilding`/`Upgrade` 时强制 `Structure.HasNearEnemies = false`。否则一只游荡僵尸在两台机器上距建筑差 0.07 格（4.05 格允许 / 3.98 格禁止）就会单边拒建 → `EntityCount`/`Gold` 分叉 → 6 秒后 `econ x3` 重建。
  - **建造预览供电圈隔离**（`SuspendLocalBuildCursorEnergyPreview` / `Restore…`）：本地建造预览的供电圈会临时往共享电网数组 `+1`，导致两端 `CanPlaceAtCell` 结论相反。回放指令期间摘掉它。
  - **收紧 5 分钟兜底重载判据**：`substantiveDiff` 从 `econCoreDiff || lifeDiff || absEntityDelta > 2` 改为 `econCoreDiff || lifeDiff || _consecutiveEconMismatch >= 3 || absEntityDelta > entityTol`，小幅实体数差不再触发全量重建。
- **新增诊断行（v6.2）**：
  - `[FPFingerprint]`：进关时打一次 `Math.Pow/Sin/Cos/Atan2/Exp/Log` 的 64 位精确位模式（`BitConverter.DoubleToInt64Bits`）。**两台机器这一行必须逐字符相同**；不同即说明是两颗 CPU 的 CRT 末位差异（`Math.Pow(x,2)` 与 `x*x` 在 20 万次采样里有 421 次 1-ULP 差），那是同机测不出来、双机必漂的来源。
  - `[Baseline]`：每次关卡加载/快照重载完成时打一次，含 `GameTime / LastLogic / LastPhys / NUpdates / timeBody` 与三个位置指纹（`physPos`/`computed`/`entityPos`）。同一标签行（`baseline-applied` / `enter-synced`）两端必须一致，否则分歧来自存档加载路径而不是模拟循环。
  - `[BucketDiff]`：每段第一次位置漂移时打一次，报告先分歧的位置桶号（桶号 = `entity.ID & (bucketCount-1)`），直接指出先分歧的实体 ID 区间。
  - `TABCOOP_TRACE_FIRST_TICKS=N`：每段同步后的前 N 个 tick 逐 tick 打全量摘要（只本地日志，不发网络），用来定位"第一个分歧 tick"。
- **失步诊断的三个层次**：`[StateChecksum] … MATCH` 是完整哈希（含位置）一致；`TRANSIENT PROJ DIFF` 是抛射物集合/状态不同但主体一致；`TRANSIENT STATE DRIFT` / `SOFT POS DRIFT` 是主体不一致但尚未到重建阈值。真正触发重建的是 `HARD DESYNC DETECTED`（经济/结构连续多次不符）或 `Auto-resync triggered after MissingOnClient`（对端实体缺失计数连续 4 次）。
- **客机掉帧缓解（v4+v5）**：`InputDelayTicks` 默认值 1→2（多 33ms 缓冲，`MinInputDelayTicks=2`）；客机追帧采用上游混合策略：lag≤3 tick 或单 tick>16ms 时保留 1-tick 异步，否则同步多 tick（`behind≥12→4/≥7→3/else 2`），减少网络抖动时的剧烈追赶掉帧。
- **IPv4 无法穿透双层 CGNAT**。`Lan` 模式是裸 TCP 直连，不做 NAT 穿透。同一局域网/虚拟局域网用 IPv4 即可；异地联机必须走 IPv6（见 §2.5、§5.5 第 7 步），或双方组虚拟局域网。
- **IPv6 需要运营商分配公网前缀**。`GetNetIPAddress` 只有 `fe80::` 时说明没有公网 IPv6（部分宽带默认关闭），此时只能靠虚拟局域网。
- **不对称可达**。若两台机器之间隔着一层路由器 NAT（`tracert` 第一跳是网关、第二跳才到对方），只有内层能连外层，外层连不进内层。这是网络拓扑决定的，mod 不做端口映射也不会自动穿透 —— 请让外层那台当房主。详见 §5.5。
- **无 UPnP 自动映射**。上游 `DirectFastPath` 里的 UPnP/STUN 只服务于 Steam P2P 链路，LAN 模式不复用；实测很多家用网关根本不响应 SSDP，且 CGNAT 下映射本身也无效。
- **防火墙规则需要管理员权限**。游戏进程默认非提权，自动放行会失败并降级为 UAC 弹窗或手动命令提示。IPv4 与 IPv6 入站都受同一规则管辖。
- **无传输加密**。TcpLoopback 上游就没有加密（原设计假定回环或 Steam 隧道），LAN 模式沿用了这一前提。在可信网络内没有问题，**不要在不可信网络上使用**（IPv6 公网直连同样明文）。
- **`IsLocalLoopbackSession` 语义收窄**。任何依赖它"判断是否为 TCP 传输"的外部代码需改用 `UnwrapTransport() is LocalTcpLoopbackTransport`。本次已同步检查 `ControlServer`、`TestingLifecyclePatches`、`AllyOverlayRenderer`，无遗漏。
- **`ActivePathSummary` 在客机侧**取连接时记录的 `_lastRemoteEndpoint`，若房主地址变化需重连才会更新。
- **双栈监听可能失败并静默降级**。极端情况下（系统禁用 IPv6、端口被独占）会回退 IPv4-only 并打 `Dual-stack listen failed (...); falling back to IPv4-only.` 警告 —— 此时异地 CGNAT 用户连不上，日志里能看到原因。

---

## 7. 与上游的关系

本改造基于上游 v0.1.5（`Negative12/TABCoopMod`）。改动集中在 4 个源文件 + 2 个测试文件 + csproj，均向后兼容：

- 所有旧公开签名保留（转发到新实现）
- Steam 与 LocalLoopback 两条既有链路行为零变化
- `AssemblyVersion` 不变，游戏 `exe.config` 无需修改

若上游后续实现 LAN，可参考本改造的取舍：复用既有传输的握手与帧格式、拆分 `IsLanSession` 判定、LAN 不挂 Chaos 包装（避免 13 字节 roster 错位）。
