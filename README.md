# TABCoopMod — 《亿万僵尸》(They Are Billions) 2~3 人合作联机 Mod（LAN 分支）

> **本仓库是 [`Negative12/TABCoopMod`](https://github.com/Negative12/TABCoopMod) 的 LAN 分支**，在 v0.1.5 基础上新增**原生局域网直连**（`CoopTransportMode.Lan`），详见 [`docs/13_LAN_MULTIPLAYER.md`](docs/13_LAN_MULTIPLAYER.md)。Steam P2P 与本地回环两条既有链路行为不变。发行版版本号 `v0.1.5-LAN`。

`TABCoopMod` 是为即时战略游戏《亿万僵尸》（They Are Billions，Steam x64 v1.0.14.29）打造的无服务器合作联机 Mod，支持 **2~3 名玩家** 共同游玩生存模式、战役与自定义地图。通过 `.NET AppDomainManager` 运行时注入与 HarmonyX 内存补丁实现，**不修改游戏 exe/dll 的磁盘字节**。

核心玩法为**执政官共控模式（Archon Mode）**：多人共同管理同一个人类基地、共享资源与科技，但每位玩家拥有完全独立的摄像机、框选、编队热键与建造上下文；主菜单内置等候大厅，支持房间码直连、Steam 好友、本地双开三种联机方式，并带 30Hz 锁步校验与自动快照重建自愈。

---

## 安装方式

### 前置要求
- Steam 正版《They Are Billions》，**64 位 `TheyAreBillions.exe` v1.0.14.29**
- 联机所有玩家必须使用**完全相同**的 Mod 版本（握手会校验版本）

### 方式 A：一键安装器（推荐）

1. 下载 [`TABCoopMod_LAN_AutoInstall.bat`](https://github.com/ouanue/TABCoopMod-LAN/releases/download/v0.1.5-LAN/TABCoopMod_LAN_AutoInstall.bat)。
2. 双击运行。若 Windows 弹出「已保护你的电脑」，点**更多信息 → 仍要运行**；弹出 UAC 时点**是**。
3. 脚本会**自动定位游戏目录**、下载最新发行包、备份原配置、解压覆盖、并放行 TCP 27099 防火墙，跑完停在窗口上显示本机地址。

### 方式 B：手动解压

1. 完全关闭游戏。
2. Steam 右键《They Are Billions》→ **管理** → **浏览本地文件**，打开游戏根目录。
3. **备份**原有的 `TheyAreBillions.exe.config`（如重命名为 `.bak`）。
4. 下载 [`TABCoopMod-v0.1.5-LAN.zip`](https://github.com/ouanue/TABCoopMod-LAN/releases/download/v0.1.5-LAN/TABCoopMod-v0.1.5-LAN.zip)，把其中文件**全部解压到游戏根目录**，提示覆盖 `TheyAreBillions.exe.config` 时选是。
5. 以管理员 PowerShell 放行防火墙（**关键**，否则队友连不上）：

   ```powershell
   New-NetFirewallRule -DisplayName "They Are Billions LAN" `
     -Direction Inbound -Protocol TCP -LocalPort 27099 -Action Allow
   ```

6. 通过 Steam 正常启动游戏，主菜单出现【多人合作模式】即成功。

### 卸载

关闭游戏后删除根目录下的 `TABCoopMod.dll`、`TABCoopMod.pdb`、`0Harmony.dll`、`steam_appid.txt`、`TABCoopMod.log`、`TABCoopMod_Logs\`、`TABCoopMod_使用说明.txt`，并还原备份的 `TheyAreBillions.exe.config.bak`（或用 Steam「验证游戏文件完整性」恢复）。

---

## 局域网联机使用

主菜单按 `K` 进入【多人合作模式】等候大厅。

### 房主
1. 点【创建局域网房间】。
2. 房间右列会显示**房间码**（形如 `192.168.1.100:27099` 或 `26.178.89.181:27099`）。
3. 点【复制房间码】，把房间码发给队友（微信 / QQ 均可）。
4. 等队友加入后，用原版【读取存档 / 新建生存游戏 / 自定义地图】界面开局。

### 客机
1. 点【加入局域网房间…】。
2. 输入框会**自动预填剪贴板里的房间码**（粘贴功能），直接点【好的】；若未自动填入，手动粘贴房间码。
3. 连上后大厅顶部显示 `链路: LAN (地址:27099)`，点【进入准备状态】，房主即可开局。

### 支持的地址写法

| 写法 | 含义 |
| :--- | :--- |
| `192.168.1.100` | 同一局域网，默认端口 27099 |
| `192.168.1.100:27099` | 同一局域网 + 显式端口 |
| `26.178.89.181` | 虚拟局域网（Radmin VPN 等）点对点地址 |
| `240e:...` | 公网 IPv6 直连 |
| `[240e:...]:27099` | 公网 IPv6 + 显式端口 |

> 大厅顶部有【测试房主连通性】按钮，只发探测包、明确回报「可达 / 超时 / 被拒绝」及原因，连不上时优先用它排查。

---

## 局域网联机需要的第三方工具

LAN 模式是**明文裸 TCP**，本身不需要任何第三方工具，只要两台机器网络可达即可。但「局域网」只在你和朋友**在同一个路由器下**才成立；一旦异地（不同家庭、不同运营商），就需要借助虚拟局域网工具把两台机器拉进同一个私有网段。

| 工具 | 说明 | 适用 |
| :--- | :--- | :--- |
| **[Radmin VPN](https://www.radmin-vpn.com/)**（推荐） | 免费、点对点组网，有图形界面，创建/加入「网络」即可，成员获得 `26.x.x.x` 地址 | 异地联机首选，本分支实测通过 |
| **[ZeroTier](https://www.zerotier.com/)** | 免费，Web 后台建网络、授权成员，获得 `10.x.x.x` / `fd` 地址 | 异地联机 |
| **[Tailscale](https://tailscale.com/)** | 基于 WireGuard，登录即组网 | 异地联机 |
| **[Hamachi](https://vpn.net/)**（LogMeIn） | 老牌虚拟局域网 | 异地联机 |
| **蒲公英 / 贝锐** | 国内组网工具 | 异地联机 |

### Radmin VPN 联机步骤（推荐）

1. 双方都安装 [Radmin VPN](https://www.radmin-vpn.com/) 并注册登录。
2. 一方在 Radmin VPN 里**创建网络**，另一方**加入网络**（输入网络名 + 密码）。
3. 加入后，Radmin VPN 窗口里会显示双方的 `26.x.x.x` 地址。
4. 房主进游戏点【创建局域网房间】，房间码此时会自动显示成 Radmin 地址（如 `26.178.89.181:27099`）——因为本分支**优先取虚拟网卡地址作为房间码**。
5. 房主复制房间码发给客机，客机【加入局域网房间…】粘贴即可。

### 为什么异地直连通常必须靠 VPN 或 IPv6

很多家庭宽带处于**双层 CGNAT**：光猫 PPPoE 拿到的是运营商内网地址（`100.64.0.0/10`），运营商侧又做了一层 NAT，导致** IPv4 入站永远不可达**，在光猫/路由器上做端口映射也没用。此时 IPv4 直连必然失败，只能二选一：

- **虚拟局域网**（Radmin VPN / ZeroTier / Tailscale 等，推荐，双方都装即可）；或
- **公网 IPv6**（本分支已原生双栈监听，双方都有公网 IPv6 时可 `[240e:...]:27099` 直连）。

判断本机是否有公网 IPv6：

```powershell
Get-NetIPAddress -AddressFamily IPv6 |
  Where-Object { $_.IPAddress -notlike 'fe80*' -and $_.IPAddress -notlike 'fd*' }
```

以 `240e:` / `2408:` / `2409:` 等 `2` 开头的是公网地址；只有 `fe80::`（链路本地）或 `fd::`（ULA）说明没有公网 IPv6。

---

## 联机方式对照

| 方式 | 入口 | 需要 Steam 登录 | 需要互为好友 | 适用场景 |
| :--- | :--- | :---: | :---: | :--- |
| **局域网直连** | 【创建局域网房间】/【加入局域网房间…】 | 否 | 否 | 同路由器 IPv4；或 Radmin/ZeroTier 等虚拟局域网；或公网 IPv6 |
| Steam 好友 | 【创建 Steam 房间】/ 接受邀请 | 是 | 是 | 异地联机（Steam 大厅做信令 + UDP 直连） |
| 同机双开测试 | 【创建本地测试房间】/【加入本地房间】 | 否 | 否 | 单机验证，带延迟/抖动/丢包模拟 |

---

## 常见问题

| 现象 | 原因与处理 |
| :--- | :--- |
| 客机提示「连接超时」 | ① 房主没点【创建局域网房间】；② 防火墙未放行 TCP 27099；③ 填的地址不可达（异地填了房主家内网地址）。先用【测试房主连通性】定位 |
| 连接被拒绝 | 房主在线但 27099 没监听，多半是房主还没开房 |
| 双方版本不一致弹窗 | 两端 Mod 版本不同，需同步更新到同一版 |
| `ping` 不通但游戏能连 | Windows 默认拦截 ICMP，属正常，以 TCP 探测为准 |
| 大厅只显示 `fe80::` / `fd::` | 本机无公网 IPv6，异地联机请改用虚拟局域网 |
| 房间码显示的是内网地址、异地连不上 | 异地联机需先加入同一虚拟局域网（Radmin 等），房间码会变为 `26.x.x.x` |

---

## 开发者构建

```powershell
# 1. 首次克隆后，从本机游戏主程序导出编译期只读引用库到 lib/（不修改游戏文件）
#    ⚠ 必须用 Windows PowerShell 5.1（powershell.exe），不能用 pwsh
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/extract_sdk.ps1

# 2.（推荐）构建 Release：自动重定向用户目录、清代理、校验产物符号
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_lan.ps1

# 3. 打包玩家发行 zip（输出到 dist/）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package_release.ps1
```

- Mod 主工程目标框架 `net46` / `x64`；`lib/` 必须位于仓库根（csproj 引用是 `..\..\lib\`）。
- 详细说明见 [`docs/13_LAN_MULTIPLAYER.md`](docs/13_LAN_MULTIPLAYER.md)（改造动机、逐处改动、双栈实现、已知限制）与 [`docs/00_DEV_GUIDE.md`](docs/00_DEV_GUIDE.md)（工程铁律与工具链规范）。

---

## 技术文档索引

| 文档 | 内容 |
| :--- | :--- |
| [`docs/13_LAN_MULTIPLAYER.md`](docs/13_LAN_MULTIPLAYER.md) | **局域网原生联机**：改造动机、传输复用、逐处改动、双栈 IPv6、房间码、已知限制 |
| [`docs/00_DEV_GUIDE.md`](docs/00_DEV_GUIDE.md) | 工程铁律、CLR / HarmonyX 踩坑手册与工具链规范 |
| [`docs/04_NETWORK_PROTOCOL.md`](docs/04_NETWORK_PROTOCOL.md) | 传输抽象、包头协议、`NetCommand` 与 `StateHasher` v2 |
| [`docs/07_TESTING_AND_MCP.md`](docs/07_TESTING_AND_MCP.md) | 自动化回放验证、Chaos 故障注入与控制通道 |
| [`docs/10_STEAM_P2P_TEST_ANALYSIS_AND_OPTIMIZATION_PLAN.md`](docs/10_STEAM_P2P_TEST_ANALYSIS_AND_OPTIMIZATION_PLAN.md) | Steam P2P 链路实测与网络兼容矩阵 |
| [`docs/12_STEAM_P2P_BUGS_AND_FIXES_20261116.md`](docs/12_STEAM_P2P_BUGS_AND_FIXES_20261116.md) | Steam P2P 缺陷与修复记录 |

---

## 赞赏支持

如果这个 Mod 给你和朋友带来了快乐，欢迎请作者喝一杯奶茶 ☕

<div align="center">

**千言万语，感谢所有人的热爱与支持！**

![林观月 的微信赞赏码](docs/assets/wechat_reward_qr.jpg)

*微信扫一扫，向 林观月 赞赏*

</div>
