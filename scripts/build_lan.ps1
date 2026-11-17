# TABCoopMod — LAN 版构建脚本
#
# 用途：在本机一键还原依赖并编译出 TABCoopMod.dll（含局域网联机功能）。
#
# 为什么需要重定向一堆目录：
#   当用户目录（%USERPROFILE%\AppData、%USERPROFILE%\.dotnet）不可写时，
#   dotnet CLI 会在三处分别抛 UnauthorizedAccessException：
#     1. WorkloadInstallerFactory  → %TEMP%\<guid>
#     2. DotnetFirstTimeUseConfigurer → %USERPROFILE%\.dotnet
#     3. NuGet 配置读取           → %APPDATA%\NuGet\NuGet.Config
#   因此必须同时重定向 TEMP / TMP / APPDATA / LOCALAPPDATA / DOTNET_CLI_HOME。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build_lan.ps1
#
# 前置条件：
#   1. 已用 scripts\extract_sdk.ps1（**Windows PowerShell 5.1**）抽出 lib\*.dll
#   2. lib\ 位于仓库根目录（csproj 里是 ..\..\lib\，相对 csproj 上两级）
#   3. 已安装 .NET SDK 8.x

param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration = 'Release',
    [string]$Dotnet = ''
)

$ErrorActionPreference = 'Stop'

$csproj = Join-Path $RepoRoot 'src\TABCoopMod\TABCoopMod.csproj'
if (-not (Test-Path $csproj)) { throw "csproj not found: $csproj" }

# --- 定位 dotnet -------------------------------------------------------------
if (-not $Dotnet) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe',
        (Join-Path (Split-Path -Parent $RepoRoot) 'dotnet-sdk\dotnet.exe')
    )
    $Dotnet = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Dotnet) {
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($cmd) { $Dotnet = $cmd.Source }
    }
}
if (-not $Dotnet -or -not (Test-Path $Dotnet)) { throw 'dotnet.exe not found. Install .NET SDK 8.x or pass -Dotnet <path>.' }

# --- 检查 lib\ (csproj 需要 6 个游戏 DLL) ------------------------------------
$libDir = Join-Path $RepoRoot 'lib'
$required = @('DXVision.dll', 'DXPlatform_Desktop.dll', 'Steamworks.NET.dll',
              'TheyAreBillions.exe', 'System.ValueTuple.dll', 'SlimDX.dll')
$missing = $required | Where-Object { -not (Test-Path (Join-Path $libDir $_)) }
if ($missing.Count -gt 0) {
    Write-Warning "lib\ is missing: $($missing -join ', ')"
    Write-Warning 'Run scripts\extract_sdk.ps1 with **Windows PowerShell 5.1** first (PowerShell 7 cannot reflect .NET Framework assemblies).'
}

# --- 重定向可写目录 ----------------------------------------------------------
$cliHome = Join-Path (Split-Path -Parent $RepoRoot) 'cli-home'
foreach ($d in @($cliHome, (Join-Path $cliHome 'tmp'),
                 (Join-Path $cliHome 'appdata'),
                 (Join-Path $cliHome 'localappdata'),
                 (Join-Path $cliHome 'nuget-packages'))) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

$env:TEMP              = Join-Path $cliHome 'tmp'
$env:TMP               = $env:TEMP
$env:APPDATA           = Join-Path $cliHome 'appdata'
$env:LOCALAPPDATA      = Join-Path $cliHome 'localappdata'
$env:DOTNET_CLI_HOME   = $cliHome
$env:NUGET_PACKAGES    = Join-Path $cliHome 'nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO     = '1'

# 本机系统代理可能是失效的（端口无监听），会让 restore 卡死。
$env:HTTP_PROXY  = ''
$env:HTTPS_PROXY = ''
$env:ALL_PROXY   = ''

# --- 还原 + 构建 -------------------------------------------------------------
Write-Host "== dotnet : $Dotnet"
Write-Host "== repo   : $RepoRoot"
Write-Host "== config : $Configuration"
Write-Host "== cliHome: $cliHome"

& $Dotnet restore $csproj 2>&1 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)" }

& $Dotnet build $csproj -c $Configuration --no-restore 2>&1 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

# --- 校验产物 ---------------------------------------------------------------
$outDir  = Join-Path $RepoRoot "src\TABCoopMod\bin\$Configuration"
$dllPath = Join-Path $outDir 'TABCoopMod.dll'
$pdbPath = Join-Path $outDir 'TABCoopMod.pdb'
if (-not (Test-Path $dllPath)) { throw "build succeeded but $dllPath not found" }

foreach ($p in @($dllPath, $pdbPath)) {
    if (Test-Path $p) {
        $fi = Get-Item $p
        Write-Host ("== {0,-18} {1,10:N0}  {2}" -f $fi.Name, $fi.Length, $fi.LastWriteTime)
    }
}

# 字符串在 DLL 里有两套堆：#US（用户字符串字面量）是 UTF-16LE，#Strings（类型/成员名）是 UTF-8。
# 所以两种编码都要试，只试一种会有假阴性。
$bytes = [System.IO.File]::ReadAllBytes($dllPath)
function Test-ByteSeq([byte[]]$haystack, [byte[]]$n) {
    for ($i = 0; $i -le $haystack.Length - $n.Length; $i++) {
        $ok = $true
        for ($j = 0; $j -lt $n.Length; $j++) {
            if ($haystack[$i + $j] -ne $n[$j]) { $ok = $false; break }
        }
        if ($ok) { return $true }
    }
    return $false
}
function Test-DllString([byte[]]$haystack, [string]$needle) {
    return (Test-ByteSeq $haystack ([System.Text.Encoding]::Unicode.GetBytes($needle))) -or
           (Test-ByteSeq $haystack ([System.Text.Encoding]::UTF8.GetBytes($needle)))
}

Write-Host '== LAN feature strings:'
foreach ($s in @('0.1.5-LAN', '创建局域网房间', '加入局域网房间', '局域网房间监听',
                 'IsLanSession', 'CoopTransportMode', 'GetLocalIPv4Addresses',
                 'ShowInputBoxMessageBox')) {
    Write-Host ("   {0,-26} {1}" -f $s, (Test-DllString $bytes $s))
}

Write-Host ''
Write-Host 'Done.'
Write-Host "  dll : $dllPath"
Write-Host '  next: back up the game''s TABCoopMod.dll, then copy dll+pdb into the game root.'
