#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Host.UI.RawUI.WindowTitle = 'TABCoopMod 局域网版 一键安装'

# ---- 本版发行包指纹（改版本时必须同步）----
$ExpectedZipSize  = 1079551
$ExpectedDllSize  = 499712
$ExpectedDllHash  = 'EC46CD1D753D395A1AC77456C6259D3562F48BE1A0E37384ACAC59A6157C3D9B'
# 只用于提示，不参与判定。zip 内部时间戳只有 2 秒精度，所以从压缩包解出来的
# 文件时间是 14:08:16，而在开发机上直接构建的是 14:08:17 —— 两者是同一个构建，
# 判定一律以 SHA256 为准。
$ExpectedBuild    = '2026-11-20 14:08:16 或 14:08:17（zip 只有 2 秒精度，两种都对）'

function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Ok($t) { Write-Host $t -ForegroundColor Green }
function Bad($t) { Write-Host $t -ForegroundColor Red }
function Warn($t) { Write-Host $t -ForegroundColor Yellow }

$ZipUrl = 'https://github.com/ouanue/TABCoopMod-LAN/releases/download/v0.1.5-LAN/TABCoopMod-v0.1.5-LAN.zip'
$ZipPath = Join-Path $env:TEMP 'TABCoopMod-v0.1.5-LAN.zip'
$WorkDir = Join-Path $env:TEMP 'tabcoop_lan_install'

Say ''
Say '==============================================' Cyan
Say '  TABCoopMod 局域网版 (v0.1.5-LAN) 一键安装' Cyan
Say '==============================================' Cyan
Say ''

# ---------- 1. 管理员 ----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Warn '需要管理员权限（要写游戏目录 + 加防火墙规则），正在请求提权...'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    $psi.Verb = 'runas'
    try { [System.Diagnostics.Process]::Start($psi) | Out-Null } catch { Bad '提权被取消，安装未完成。'; Read-Host '按回车退出'; exit 1 }
    exit 0
}
Ok '[1/6] 管理员权限：OK'

# ---------- 2. 找游戏目录 ----------
Say ''
Say '[2/6] 正在查找《They Are Billions》安装目录 ...'
$candidates = New-Object System.Collections.Generic.List[string]

try {
    $steamKey = Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction Stop
    if ($steamKey.SteamPath) {
        $steamRoot = $steamKey.SteamPath -replace '/', '\'
        $candidates.Add((Join-Path $steamRoot 'steamapps\common\They Are Billions'))
        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $lib = $m.Groups[1].Value -replace '\\\\', '\'
                $candidates.Add((Join-Path $lib 'steamapps\common\They Are Billions'))
            }
        }
    }
} catch { }

foreach ($drv in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -match '^[A-Z]:\\$' })) {
    $candidates.Add((Join-Path $drv.Root 'SteamLibrary\steamapps\common\They Are Billions'))
    $candidates.Add((Join-Path $drv.Root 'Steam\steamapps\common\They Are Billions'))
    $candidates.Add((Join-Path $drv.Root 'Program Files (x86)\Steam\steamapps\common\They Are Billions'))
    $candidates.Add((Join-Path $drv.Root 'Games\Steam\steamapps\common\They Are Billions'))
    $candidates.Add((Join-Path $drv.Root 'Game\Steam\steamapps\common\They Are Billions'))
}

$gameDir = $null
foreach ($c in $candidates) {
    if ($c -and (Test-Path (Join-Path $c 'TheyAreBillions.exe'))) { $gameDir = $c; break }
}

if (-not $gameDir) {
    # 退路：搜 Steam 库清单
    foreach ($drv in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -match '^[A-Z]:\\$' })) {
        foreach ($acf in (Get-ChildItem -Path $drv.Root -Filter 'appmanifest_644930.acf' -Recurse -Depth 4 -ErrorAction SilentlyContinue)) {
            $d = $acf.Directory.Parent.FullName
            $cand = Join-Path $d 'common\They Are Billions'
            if (Test-Path (Join-Path $cand 'TheyAreBillions.exe')) { $gameDir = $cand; break }
        }
        if ($gameDir) { break }
    }
}

if (-not $gameDir) {
    Bad '没有自动找到游戏目录。'
    Say ''
    Say '请手动输入游戏目录（就是里面有 TheyAreBillions.exe 的那个文件夹），例如：'
    Say '  D:\Steam\steamapps\common\They Are Billions'
    $gameDir = Read-Host '游戏目录'
    $gameDir = $gameDir.Trim('"').Trim()
    if (-not (Test-Path (Join-Path $gameDir 'TheyAreBillions.exe'))) {
        Bad "这个目录里没有 TheyAreBillions.exe：$gameDir"
        Read-Host '按回车退出'; exit 1
    }
}
Ok "游戏目录：$gameDir"

$exeVer = (Get-Item (Join-Path $gameDir 'TheyAreBillions.exe')).VersionInfo.FileVersion
Say "游戏版本：$exeVer"
if ($exeVer -and $exeVer -notlike '1.0.14.29*') {
    Warn '注意：本 mod 只在 1.0.14.29 上验证过，版本不同可能不加载。'
}

# ---------- 3. 关键：游戏必须已完全退出 ----------
Say ''
Say '[3/6] 检查游戏是否已退出 ...'
$running = @(Get-Process -Name 'TheyAreBillions' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Bad ''
    Bad '  游戏还在运行，安装已中止。'
    Bad ''
    Say '  原因：游戏运行时 TABCoopMod.dll 被进程锁住，覆盖会失败；'
    Say '        而 Copy-Item -Force 在这种情况下不一定报错，'
    Say '        结果就是"看起来装好了，其实还是旧版"。'
    Say ''
    Say '  请这样做：'
    Say '    1) 关掉游戏（回主菜单不算，必须退出进程）'
    Say '    2) Ctrl+Shift+Esc 打开任务管理器 -> 详细信息'
    Say '    3) 确认列表里没有 TheyAreBillions.exe'
    Say '    4) 重新运行本安装程序'
    Say ''
    foreach ($p in $running) { Say ("  仍在运行的进程：PID {0}，启动于 {1}" -f $p.Id, $p.StartTime) }
    Say ''
    Read-Host '按回车退出'
    exit 1
}
Ok '  游戏未运行，可以安全覆盖。'

# ---------- 4. 下载 ----------
Say ''
Say '[4/6] 正在下载安装包（约 1 MB）...'
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue }
$downloaded = $false
for ($i = 1; $i -le 3 -and -not $downloaded; $i++) {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $ZipUrl -OutFile $ZipPath -UseBasicParsing -TimeoutSec 120
        $downloaded = $true
    } catch {
        Warn "第 $i 次下载失败：$($_.Exception.Message)"
        Start-Sleep -Seconds 2
    }
}
if (-not $downloaded -or -not (Test-Path $ZipPath)) {
    Bad '下载失败。请检查网络，或让队友直接把 TABCoopMod-v0.1.5-LAN.zip 传给你，'
    Say '然后把它放到 %TEMP% 目录下再重新运行本程序。'
    Read-Host '按回车退出'; exit 1
}
$size = (Get-Item $ZipPath).Length
Ok ("下载完成：{0:N0} 字节" -f $size)
if ($size -ne $ExpectedZipSize) {
    Warn ("注意：大小与预期不同（预期 {0:N0} 字节）。可能下载到了旧版本或被截断。" -f $ExpectedZipSize)
    Say  '      继续安装会在最后一步做哈希校验，届时会明确告诉你对不对。'
}

# ---------- 5. 解压安装 ----------
Say ''
Say '[5/6] 正在安装到游戏目录 ...'
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
try {
    Expand-Archive -Path $ZipPath -DestinationPath $WorkDir -Force
} catch {
    Bad "解压失败：$($_.Exception.Message)"
    Read-Host '按回车退出'; exit 1
}

$files = @('TABCoopMod.dll','TABCoopMod.pdb','0Harmony.dll','steam_appid.txt','TheyAreBillions.exe.config','TABCoopMod_使用说明.txt')
foreach ($f in $files) {
    $src = Join-Path $WorkDir $f
    if (-not (Test-Path $src)) { Warn "安装包里缺少 $f（跳过）"; continue }
    $dst = Join-Path $gameDir $f
    if ($f -in @('TABCoopMod.dll','TABCoopMod.pdb','TheyAreBillions.exe.config','0Harmony.dll')) {
        if ((Test-Path $dst) -and -not (Test-Path ($dst + '.upstream-v0.1.5.bak'))) {
            Copy-Item $dst ($dst + '.upstream-v0.1.5.bak') -Force
            Say "  已备份原文件：$f.upstream-v0.1.5.bak"
        }
    }
    Copy-Item $src $dst -Force
    Ok "  已安装 $f"
}

# ---------- 6. 安装后校验 ----------
Say ''
Say '[6/6] 正在校验安装结果 ...'
$dllPath = Join-Path $gameDir 'TABCoopMod.dll'
$dllOk = $false
if (Test-Path $dllPath) {
    $dllSize = (Get-Item $dllPath).Length
    $dllHash = (Get-FileHash $dllPath -Algorithm SHA256).Hash
    if ($dllSize -eq $ExpectedDllSize -and $dllHash -eq $ExpectedDllHash) {
        $dllOk = $true
        Ok '  TABCoopMod.dll 校验通过（新版本已就位）'
        Say ("    大小 {0:N0} 字节，SHA256 {1}" -f $dllSize, $dllHash)
        Say ("    日志头 Build= 应为 {0}" -f $ExpectedBuild)
    } else {
        Bad '  校验失败：装进去的 TABCoopMod.dll 不是这一版。'
        Say ("    实际大小 {0:N0} 字节（预期 {1:N0}）" -f $dllSize, $ExpectedDllSize)
        Say ("    实际哈希 {0}" -f $dllHash)
        Say ("    预期哈希 {0}" -f $ExpectedDllHash)
        Say ''
        Say '  常见原因：'
        Say '    - 游戏没完全退出（本程序已检查过，但仍可能是 Steam 后台重新拉起了游戏）'
        Say '    - 杀毒软件拦截了文件写入'
        Say '    - 游戏目录是只读的，或没有管理员权限'
    }
} else {
    Bad "  校验失败：$dllPath 不存在。"
}

# ---------- 防火墙 ----------
Say ''
Say '正在放行防火墙 TCP 27099 ...'
$ruleName = 'TABCoopMod LAN 27099'
$exists = $false
try {
    netsh advfirewall firewall show rule name="$ruleName" | Out-Null
    if ($LASTEXITCODE -eq 0) { $exists = $true }
} catch { }
if ($exists) {
    Ok '  防火墙规则已存在'
} else {
    netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=TCP localport=27099 profile=any | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok '  已放行入站 TCP 27099' } else { Warn '  放行失败，请手动在 Windows 防火墙里放行 27099' }
}

# ---------- 结果 ----------
Say ''
Say '==============================================' Cyan
if ($dllOk) { Say '  安装完成，校验通过' Cyan } else { Say '  安装未完成（见上面的校验失败信息）' Cyan }
Say '==============================================' Cyan
Say ''

$markers = New-Object System.Collections.Generic.List[object]
foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    if ($ni.OperationalStatus -ne 'Up') { continue }
    if ($ni.NetworkInterfaceType -eq 'Loopback') { continue }
    $desc = ($ni.Name + ' ' + $ni.Description)
    $virt = $desc -match '(?i)radmin|zerotier|tailscale|hamachi|wireguard|openvpn|tap-|tun|vmware|virtualbox|vbox|hyper-v|vethernet|npcap|wintun|softether|pgyvpn|pugongying|蒲公英|parallels|clash|nekoray|mihomo'
    foreach ($ua in $ni.GetIPProperties().UnicastAddresses) {
        $ip = $ua.Address
        if ($ip.AddressFamily -eq 'InterNetwork') {
            $kind = if ($ip.ToString().StartsWith('169.254.')) { 'invalid' } elseif ($virt) { 'virtual' } else { 'lan' }
            $markers.Add([pscustomobject]@{ Kind=$kind; Address=$ip.ToString(); Adapter=$ni.Name }) | Out-Null
        } elseif ($ip.AddressFamily -eq 'InterNetworkV6') {
            $s = $ip.ToString()
            if ($ip.IsIPv6LinkLocal) { continue }
            if ($s.StartsWith('fd') -or $s.StartsWith('fc')) { continue }
            $markers.Add([pscustomobject]@{ Kind='ipv6'; Address=$s; Adapter=$ni.Name }) | Out-Null
        }
    }
}

$v6 = @($markers | Where-Object { $_.Kind -eq 'ipv6' })
$lan = @($markers | Where-Object { $_.Kind -eq 'lan' })
$virt = @($markers | Where-Object { $_.Kind -eq 'virtual' })

Say '本机地址：'
$markers | Select-Object @{n='类型';e={ switch ($_.Kind) { 'lan' {'物理网卡'} 'virtual' {'虚拟网卡'} 'invalid' {'无效地址'} 'ipv6' {'IPv6'} } }},Address,Adapter | Format-Table -AutoSize | Out-String | Write-Host

if ($v6.Count -gt 0) {
    Say '>>> 异地联机（对方不在你家）：把下面这行 IPv6 发给房主/队友：' Green
    Say ("    " + $v6[0].Address + ":27099") Yellow
} else {
    Warn '>>> 本机没有公网 IPv6。异地联机只能改用虚拟局域网（Radmin VPN / ZeroTier 等）。'
}
if ($lan.Count -gt 0) {
    Say ('>>> 同一个路由器下（同一局域网）：用 ' + $lan[0].Address + ':27099') Green
}
Say ''
Say '接下来：'
Say '  1) 打开 Steam 里的 They Are Billions'
Say '  2) 主菜单点【多人合作模式】（或游戏里按 K）'
Say '  3) 房主点【创建局域网房间】，把上面那行地址发给队友'
Say '  4) 队友点【加入局域网房间…】，粘贴/输入那行地址'
Say '  5) 连不上就先点【测试局域网房主连通性】看具体原因'
Say ''
Say '联机后请把游戏目录下的 TABCoopMod.log 发回给开发者。'
Say '本版日志里每个校验周期都带 phys=物理步数，是这次排查的关键数据。'
Say ''
Say '卸载：删掉游戏目录里的 TABCoopMod.dll / TABCoopMod.pdb / 0Harmony.dll /'
Say '      steam_appid.txt / TABCoopMod_使用说明.txt，并把'
Say '      TheyAreBillions.exe.config.upstream-v0.1.5.bak 改回 TheyAreBillions.exe.config'
Say ''
Read-Host '按回车退出'
