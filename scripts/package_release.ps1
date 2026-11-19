# package_release.ps1
# Builds TABCoopMod (Release) and packages a drag-and-drop archive into dist/
# containing ONLY runtime files + a plain-text user guide (no install scripts).

param(
    [string]$WorkspaceDir = "",
    [string]$Version = "v0.1.5-LAN"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($WorkspaceDir)) {
    $WorkspaceDir = Split-Path -Parent $PSScriptRoot
}

Write-Host "[1/3] Building TABCoopMod (Release)..."
dotnet build "$WorkspaceDir\src\TABCoopMod\TABCoopMod.csproj" -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed!"
}

$binDir = Join-Path $WorkspaceDir "src\TABCoopMod\bin\Release"
$distRoot = Join-Path $WorkspaceDir "dist"
$pkgName = "TABCoopMod-$Version"
$stageDir = Join-Path $distRoot $pkgName
$zipPath = Join-Path $distRoot "$pkgName.zip"

Write-Host "[2/3] Staging release files in $stageDir..."
if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Copy-Item (Join-Path $binDir "TABCoopMod.dll") (Join-Path $stageDir "TABCoopMod.dll") -Force
Copy-Item (Join-Path $binDir "TABCoopMod.pdb") (Join-Path $stageDir "TABCoopMod.pdb") -Force
Copy-Item (Join-Path $binDir "0Harmony.dll") (Join-Path $stageDir "0Harmony.dll") -Force

[System.IO.File]::WriteAllText((Join-Path $stageDir "steam_appid.txt"), "644930")

$asmVersion = "0.1.5.0"
if ($Version -match '(\d+)\.(\d+)\.(\d+)') {
    $asmVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
}

$configXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.6"/>
  </startup>
  <runtime>
    <appDomainManagerAssembly value="TABCoopMod, Version=$asmVersion, Culture=neutral, PublicKeyToken=null" />
    <appDomainManagerType value="TABCoopMod.Bootstrap.ModAppDomainManager" />
  </runtime>
</configuration>
"@
[System.IO.File]::WriteAllText((Join-Path $stageDir "TheyAreBillions.exe.config"), $configXml, [System.Text.Encoding]::UTF8)

$guideText = @"
====================================================================
  TABCoopMod ($Version) — 《亿万僵尸》2~3人合作联机 Mod 使用说明
====================================================================

【适用版本】
  仅支持 Steam 正版《They Are Billions》64位版 (v1.0.14.29)。
  所有联机玩家必须安装完全相同的 Mod 版本。


【一、安装方法（解压即用）】
  1. 完全关闭正在运行的《They Are Billions》游戏。
  2. 打开 Steam 客户端 -> 右键《They Are Billions》-> 管理 -> 浏览本地文件，
     打开游戏根目录（即包含 TheyAreBillions.exe 的文件夹）。
  3. 【强烈建议先备份】将游戏根目录中原有的 TheyAreBillions.exe.config
     复制一份命名为 TheyAreBillions.exe.config.bak（方便日后卸载还原）。
  4. 将本压缩包内的所有文件直接解压复制到游戏根目录，提示替换
     TheyAreBillions.exe.config 时选择“替换/覆盖”。

  解压后游戏根目录应包含以下新增/更新文件：
    - TABCoopMod.dll
    - TABCoopMod.pdb
    - 0Harmony.dll
    - steam_appid.txt
    - TheyAreBillions.exe.config   (已替换)
    - TABCoopMod_使用说明.txt


【二、如何开始联机（执政官共控模式）】
  1. 像平时一样从 Steam 启动《They Are Billions》。
  2. 在主菜单可以看到新增按钮【多人合作模式】（快捷键 K），点击进入等候大厅。
  3. Steam 好友联机：
     - 房主：点击【创建 Steam 房间】，进入等候房间后可点击邀请按钮或通过
       Steam 界面（Shift+Tab）邀请好友。
     - 客机：直接接受房主的 Steam 邀请即可自动加入（如果当前未启动游戏，
       可通过 Steam 邀请启动并自动接入）。
     - 自动直连加速：通过 Steam 大厅接头后，Mod 会自动尝试局域网/公网 IPv6/STUN UDP
       直连升级并在大厅顶部实时显示链路与 RTT；若双方网络受限无法直连，
       会自动通过隔离后的 Steam 多通道保底。
     - 同机双开本地测试：房主点击【创建本地测试房间】（127.0.0.1:27099），
       另一窗口点击【加入本地房间】即可。
  4. 局域网直连（无需 Steam 登录，无需加好友）：
     - 适用：两台机器接在同一个路由器下，或已组好 Radmin / ZeroTier /
       Tailscale / 蒲公英 等虚拟局域网；异地联机请用 IPv6（见下）。
     - 房主：点击【创建局域网房间】。按钮下方蓝色方块会显示可用地址：
         「异地联机把 IPv6 告诉队友: 240e:345:6789:abcd:1234:5678:9abc:def0」
         「同一局域网用 IPv4: 192.168.1.100」
       把这行地址和端口 27099 告诉队友。
     - 客机：点击【加入局域网房间…】，在弹出的输入框里填房主的地址。
       支持 192.168.1.100 、192.168.1.100:27099 、
       240e:345:6789:abcd:1234:5678:9abc:def0 、[240e:345:6789::def0]:27099 四种写法。
     - 大厅顶部显示“链路: LAN (地址:27099)”即表示已走局域网直连。
     - 不确定能不能通时，先点【测试房主连通性】：它只发一个探测包，
       会明确告诉你「可达 / 超时 / 被拒绝」以及对应的解决办法。
     - 前置条件：
       · 两台机器的 Mod 版本必须完全一致（都是本版本 v0.1.5-LAN）。
       · 房主防火墙需放行 TCP 27099，可用管理员 PowerShell 执行：
         New-NetFirewallRule -DisplayName "They Are Billions LAN" ``
           -Direction Inbound -Protocol TCP -LocalPort 27099 -Action Allow
         （创建房间时 Mod 会自动尝试放行，失败会弹出确认框；IPv6 入站
           同样受此规则管辖，也必须放行。）
       · 若本机有多张网卡（虚拟机、VPN、Radmin 等），大厅会优先列出物理
         网卡；队友连不上时，把「虚拟网卡(VPN)」里的地址也试一遍。
  4.1 异地联机（不在同一个路由器下）—— 请用 IPv6：
     - 上一版只能填 IPv4，但很多家庭宽带是「双层 CGNAT」：
       光猫 WAN 拿到的是 100.64.x.x（运营商内网地址），运营商侧还做了一层
       NAT。这种情况下 IPv4 入站永远不可达，在光猫上做端口映射也没用。
     - 判断方法：PowerShell 运行
         curl.exe https://ipv4.icanhazip.com
       如果返回的公网 IP 与光猫 WAN 口地址不同，就是双层 CGNAT。
     - 破解办法是走 IPv6（本版本已原生支持，双栈监听）：
       查看本机有没有公网 IPv6：
         Get-NetIPAddress -AddressFamily IPv6 | Where-Object { `$_.IPAddress -notlike 'fe80*' -and `$_.IPAddress -notlike 'fd*' }
       以 2 开头（如 240e:345:...）的就是公网地址。
     - 房主大厅里“异地联机把 IPv6 告诉队友”那一行就是要发给对方的地址；
       客机把它整串粘进输入框即可（带端口要写成 [240e::1]:27099）。
     - 若本机只有 fe80:: 开头的地址，说明宽带没有分配公网 IPv6，
       此时只能用「虚拟局域网」方案（双方装同一个 ZeroTier / Tailscale /
       Radmin 网络，然后填虚拟网卡地址）。
     - 注意：LAN 模式是明文裸 TCP，仅适用于可信网络，不要在公共网络上使用。
  5. 房间准备与开局：
     - 客机点击【进入准备状态】后，房主即可点击【读取存档...】、【新建生存游戏...】
       或【自定义地图...】开启对局。
     - 房主可在房间右侧切换新局预设：【联机均衡】（2P/3P 扩图与资源/尸潮配平）
       或【原版】。
  6. 局内协同功能：
     - 2~3 名玩家共控同一基地与共享资源，但各自拥有独立视角、框选、编队与 UI。
     - 小地图实时显示队友彩色视野框（P1 蓝 / P2 绿 / P3 橙）。
     - 战术信号打点：按住 Alt + 鼠标左键 点击地面，可向队友同步发送地图信号。
     - 同步暂停：按空格或点击右上角暂停按钮可同步冻结对局（支持暂停期间微操），
       恢复时会有 3 秒顶居中倒计时提示。


【三、卸载方法】
  1. 关闭游戏。
  2. 删除游戏根目录下的以下文件：
       TABCoopMod.dll
       TABCoopMod.pdb
       0Harmony.dll
       steam_appid.txt
       TABCoopMod.log（如存在）
       TABCoopMod_Logs\ 目录（如存在）
       TABCoopMod_使用说明.txt
  3. 还原 TheyAreBillions.exe.config：
     - 若此前备份了 TheyAreBillions.exe.config.bak，删掉当前的
       TheyAreBillions.exe.config 并把 .bak 改回原名即可；
     - 若未备份，可在 Steam 中右键游戏 -> 属性 -> 已安装文件 ->
       “验证游戏文件的完整性”自动恢复原版文件，或将 TheyAreBillions.exe.config
       内容改回以下 3 行原版内容：

<?xml version="1.0" encoding="utf-8"?>
<configuration>
<startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.6"/></startup></configuration>


【四、常见问题与排查】
  1. 启动游戏后主菜单没有【多人合作模式】按钮：
     - 请确认解压目标目录正确（必须与 TheyAreBillions.exe 在同一层，
       不要多套一层子文件夹）。
     - 请确认 TheyAreBillions.exe.config 已被成功覆盖，且与 TABCoopMod.dll、
       0Harmony.dll 位于同一目录。
  2. 启动无反应或闪退：
     - 本 Mod 仅适配 64 位版本 TheyAreBillions.exe（不要运行 _x86.exe）。
     - 若发生异常，请查看游戏根目录生成的 TABCoopMod.log（以及 TABCoopMod_Logs\ 下按版本归档的历史日志）定位原因。
  3. 杀毒软件误报：
     - Mod 通过 .NET AppDomainManager 在内存中加载 Harmony 补丁（不会修改
       TheyAreBillions.exe 本体），如被拦截请将游戏目录加入白名单。
    4. 联机每隔一两分钟就「卡一下 / 断线重连」：
       - 这是锁步失步后的自动快照重建（日志 Auto-resync / MissingOnClient），
         不是掉线。根因在引擎 DXVision 的并行循环 DXHelper_Task.For：它按核数
         把循环体切块多线程跑，共享累加器/物理广相插入的访问顺序随线程调度变化，
         两台机器浮点舍入不同，逐 tick 累积成位置漂移。
       - 本版本用 Harmony 把 DXHelper_Task.For 在确定性沙箱内改成串行循环
         （物理两段循环 + 逻辑 tick 本身），A/A 对照（同二进制、同存档、
         同种子、零输入）从「约一半运行会漂移」变为连续多轮全绿。
       - 想要更高帧率可以给游戏设环境变量 TABCOOP_SERIALIZE_LOGIC=0，
         关掉逻辑 tick 的串行化（物理串行不可关，关掉必然漂移）。
       - 如需强制同步单线程，可给游戏设环境变量 TABCOOP_DISABLE_ASYNC_TICK=1。
    5. 联机时只有十几帧：
       - 锁步帧率由「每帧跑几个逻辑 tick」决定，与显卡/网络好坏无关。
       - 串行化并行循环会让物理步进变慢：17k 实体的后期存档实测每帧 55~80ms
         （约 12~17fps），这是用帧率换确定性。
       - 客机端 InputDelayTicks 已从 1 提到 2、追赶节奏调平缓；客机落后多
         tick 时会跑 2~4 tick 追帧，帧率降到 10~15fps 属正常追赶，追上后恢复。
"@
[System.IO.File]::WriteAllText((Join-Path $stageDir "TABCoopMod_使用说明.txt"), $guideText, [System.Text.Encoding]::UTF8)

Write-Host "[3/3] Creating zip archive at $zipPath..."
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Release package created successfully!"
Get-ChildItem $stageDir | Select-Object Name, Length
Get-Item $zipPath | Select-Object FullName, Length
