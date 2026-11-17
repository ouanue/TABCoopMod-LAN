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
| `Lan` | `IPAddress.Any` | **不包装** | `IsLanSession` |

---

## 3. 逐处改动

### 3.1 `Networking/INetworkTransport.cs`

新增字段与构造：

```csharp
private readonly IPAddress _bindAddress;
private readonly int _port;
private string _lastRemoteEndpoint = "127.0.0.1";

public LocalTcpLoopbackTransport() : this(IPAddress.Loopback, DefaultPort) { }

public LocalTcpLoopbackTransport(IPAddress bindAddress, int port)
{
    _bindAddress = bindAddress ?? IPAddress.Loopback;
    _port = port > 0 && port <= 65535 ? port : DefaultPort;
}
```

`StartHost` 原本：

```csharp
_listener = new TcpListener(IPAddress.Loopback, DefaultPort);
```

改为：

```csharp
_listener = new TcpListener(_bindAddress, _port);
```

新增成员：

```csharp
public bool IsLanBound => _bindAddress != null && !IPAddress.IsLoopback(_bindAddress);
public int Port => _port;
public IPAddress BindAddress => _bindAddress;

/// <summary>枚举本机可用于联机的 IPv4（Up 状态、非 Loopback、排除 127.*）。</summary>
public static List<string> GetLocalIPv4Addresses();
```

`ConnectToHost` 补了一个原本缺失的分支：上游只认 `"host:port"`，传入纯 `"192.168.3.10"` 会被忽略。现在：

```csharp
if (parts.Length == 2 && int.TryParse(parts[1], out int parsed) && parsed > 0 && parsed <= 65535)
{
    host = parts[0].Trim();
    port = parsed;
}
else
{
    host = targetAddressOrSteamId.Trim();   // 纯 IP / 纯主机名
}
_lastRemoteEndpoint = $"{host}:{port}";
```

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
| 连接超时 | `连接超时（{n} ms 内没有任何响应），通常是对方防火墙拦掉了 TCP 27099` |
| `ConnectionRefused` | `连接被拒绝（对方主机在线但 27099 端口没有监听）` |
| `HostUnreachable` | `网络不可达（该 IP 与本机不在同一网络）` |
| 连上无回包 | `TCP 已连上，但对方没有回应探测包（端口被别的程序占用）` |
| 包类型不符 | `对方端口有响应，但不是本 mod 的房主（返回包类型={pktType}）` |

`BuildLanAddressHint()` 输出形如：

```
局域网房间监听 0.0.0.0:27099
把本机 IP 告诉队友: 192.168.1.100
虚拟网卡(VPN): 虚拟网卡/Radmin VPN (26.178.89.181)
防火墙: 未能自动放行（需要管理员权限）
管理员命令: netsh advfirewall firewall add rule name="TABCoopMod LAN 27099" dir=in ...
```

`JoinLanRoomClicked()` 预填**物理网卡**同网段前缀（`GetPreferredLanAddress()`，不再取 `localIps[0]`），减少手输：

```csharp
GameBridge.ShowInputBoxMessageBox(
    "加入局域网房间",
    "请输入房主的局域网 IP（默认端口 27099）\n支持 192.168.1.100 或 192.168.1.100:27099 两种写法。",
    suggested, 64, onOk, onCancel, validator, "AllCharactersExceptNewLines");
```

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
| 跨网卡可达 | `Test-NetConnection 192.168.3.10:27099` = True；虚拟网卡 `26.x.x.x:27099` = True |
| 跨进程握手 | 客机日志 `HandshakeAccepted! LocalPlayerId=2, Peers=2, HostVer=0.1.5-LAN` |
| 双端房间状态 | 两端 `peers` 均含 2 人，客机 `ready=true`，房主 `canStart=true` |
| UI 显示 | 客机 `角色: 客机 P2 \| 链路: LAN (192.168.3.10:27099) \| RTT: 22ms` |
| 纯手动入口 | 不借助任何钩子，点【创建局域网房间】即监听 `0.0.0.0:27099` |
| 引擎输入框 | 点【加入局域网房间…】正常弹出，预填网段前缀，可编辑可确认 |
| 绑定完整性 | `ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing)`，`Classes OK=64, failed-tolerant=0` |
| 构建 | 0 warning / 0 error |
| **地址排序**（v2） | 日志 `LAN host ready. Share this address with clients: 192.168.3.10, 26.178.89.181, 169.254.16.41, 169.254.101.12:27099` — 物理 WLAN 排首位，虚拟网卡与 APIPA 在后 |
| **连通性探测**（v2） | `probe_lan 192.168.3.10:27099` → `✅ 可达 — 房主: HostPlayer，人数 1/3，等候大厅中` |
| **失败诊断**（v2） | `probe_lan 192.168.1.3:27099` → `❌ 无响应 — 连接超时（1200 ms 内没有任何响应），通常是对方防火墙拦掉了 TCP 27099，或该 IP 不是房主` |

### 5.2 尚未验证（交由真实双机测试）

1. **跨物理机**。上述握手是同机双进程经真实局域网 IP 完成的（真套接字、真网卡绑定，非回环捷径），协议层面等价，但未经过真实路由/交换/防火墙/ARP。
2. **"UI 点按钮 → 输 IP → 成功进房"这条完整人工链路**。组件各自已验证（按钮可用、输入框可弹可编辑、`JoinSessionLan` 可由钩子成功连接），但三者串起来一次性走通的记录尚未留痕。
3. **进游戏后的同步**。只验证到"进大厅"；真实开局、快照分块传输、30Hz 锁步在 LAN 上的表现未测。同步逻辑本身未改动（本次只改连接建立与绑定地址），故预期不劣于回环模式。
4. **UAC 提权路径**。`EnsureFirewallOpenElevated()` 的 UAC 弹窗在自动化环境里无法点按，只验证到非提权路径失败并正确降级为提示手动命令。

---

## 5.5 双机连不上时的排查顺序

局域网模式的"连不上"绝大多数不是 mod 的问题。按下面顺序查，每一步都能得到确定答案：

1. **先弄清谁该当房主**。执行 `tracert -d <对方IP>`：如果第一跳就是自己的网关、第二跳才是对方，说明两台机器中间隔着路由器 NAT —— **只能由外层（离公网更近）那台当房主，内层那台出站连接**。内层当房主时，外层永远连不进来（除非做端口映射）。
2. **确认 IP 类型**。房主在自己的大厅左栏看到 `把本机 IP 告诉队友: 192.168.x.x` 才是可用地址。`169.254.*` 是网卡没拿到 DHCP，无效；`26.*` / `10.*` 常是 Radmin/ZeroTier 虚拟网卡，只在双方都装了同一 VPN 时才通。
3. **用【测试局域网房主连通性】**。这一步只发一个探测包，不占端口也不建房。三条典型输出：

   | 输出 | 含义 | 下一步 |
   | --- | --- | --- |
   | `✅ ... 可达` | 网络与端口都通 | 直接点【加入局域网房间…】 |
   | `❌ 连接超时` | 包被丢弃 | 房主放行防火墙（见下）或 IP 填错 |
   | `❌ 连接被拒绝` | 主机在线但没人监听 | 房主还没点【创建局域网房间】 |

4. **房主放行防火墙**。点【创建局域网房间】后 mod 会自动尝试，失败会弹 UAC 询问。若当时选了"否"，以**管理员**身份运行：

   ```powershell
   netsh advfirewall firewall add rule name="TABCoopMod LAN 27099" dir=in action=allow protocol=TCP localport=27099
   ```

   放行后大厅左栏会显示 `防火墙: 已放行`。

5. **`ping` 不通不代表网络不通**。Windows 默认丢弃 ICMP，所以 `ping` 超时完全正常；判断依据永远是 TCP（第 3 步的探测，或 `Test-NetConnection <IP> -Port 27099`）。

6. **实在不在同一网络**就用虚拟局域网兜底：双方装同一个 ZeroTier / Tailscale / Radmin 网络，然后填虚拟网卡地址。此时两端都在同一虚拟的二层网络里，防火墙仍是唯一门槛。

---

## 6. 已知限制

- **两台版本必须一致**（都是 `v0.1.5-LAN`）。混装上游客机，握手报版本不匹配。
- **端口固定 27099**，暂不可配。UI 未暴露端口输入；`StartHostSessionLan(int port)` 已在 API 层留了口子。
- **无 NAT 穿透**。LAN 模式是裸 TCP 直连，只适用于同一局域网或已组好的虚拟局域网（Radmin / ZeroTier / Tailscale / 蒲公英）。跨公网需要端口转发。
- **不对称可达**。若两台机器之间隔着一层路由器 NAT（`tracert` 第一跳是网关、第二跳才到对方），只有内层能连外层，外层连不进内层。这是网络拓扑决定的，mod 不做端口映射也不会自动穿透 —— 请让外层那台当房主。详见 §5.5。
- **无 UPnP 自动映射**。上游 `DirectFastPath` 里的 UPnP/STUN 只服务于 Steam P2P 链路，LAN 模式不复用；实测很多家用网关根本不响应 SSDP。
- **防火墙规则需要管理员权限**。游戏进程默认非提权，自动放行会失败并降级为 UAC 弹窗或手动命令提示。
- **无传输加密**。TcpLoopback 上游就没有加密（原设计假定回环或 Steam 隧道），LAN 模式沿用了这一前提。在可信局域网内没有问题，**不要在不可信网络上使用**。
- **`IsLocalLoopbackSession` 语义收窄**。任何依赖它"判断是否为 TCP 传输"的外部代码需改用 `UnwrapTransport() is LocalTcpLoopbackTransport`。本次已同步检查 `ControlServer`、`TestingLifecyclePatches`、`AllyOverlayRenderer`，无遗漏。
- **`ActivePathSummary` 在客机侧**取连接时记录的 `_lastRemoteEndpoint`，若房主地址变化需重连才会更新。

---

## 7. 与上游的关系

本改造基于上游 v0.1.5（`Negative12/TABCoopMod`）。改动集中在 4 个源文件 + 2 个测试文件 + csproj，均向后兼容：

- 所有旧公开签名保留（转发到新实现）
- Steam 与 LocalLoopback 两条既有链路行为零变化
- `AssemblyVersion` 不变，游戏 `exe.config` 无需修改

若上游后续实现 LAN，可参考本改造的取舍：复用既有传输的握手与帧格式、拆分 `IsLanSession` 判定、LAN 不挂 Chaos 包装（避免 13 字节 roster 错位）。
