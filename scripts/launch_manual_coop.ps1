# launch_manual_coop.ps1
# Run this script from YOUR OWN Windows terminal or Explorer (launch_manual_coop.bat)
# so both game windows open on your visible desktop (WinSta0\Default).
# It launches Host + Client, auto-connects them via Loopback, loads the save,
# and optionally tiles Host (left) and Client (right) side-by-side if Windowed (e.g. 1280x720).

param(
    [string]$SavePath = "",
    [string]$GameDir = "",
    [switch]$TileSideBySide
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
if ([string]::IsNullOrWhiteSpace($SavePath)) { $SavePath = Resolve-TabSavePath "1_Backup.zxsav" }
$exe = Join-Path $GameDir "TheyAreBillions.exe"
$hostLog = Join-Path $GameDir "TABCoopMod.host.log"
$clientLog = Join-Path $GameDir "TABCoopMod.client.log"

if (-not (Test-Path $exe)) { Write-Error "Game exe not found: $exe"; exit 2 }
if (-not (Test-Path $SavePath)) { Write-Error "Save file not found: $SavePath"; exit 2 }

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Remove-Item $hostLog, $clientLog -Force -ErrorAction SilentlyContinue

Write-Host "[manual-coop] Launching HOST (control=27901)..."
Remove-Item Env:TABCOOP_* -ErrorAction SilentlyContinue
$env:TABCOOP_TEST = "1"
$env:TABCOOP_ROLE = "host"
$env:TABCOOP_LOG_NAME = "TABCoopMod.host.log"
$env:TABCOOP_AUTO_LOAD_SAVE = $SavePath
$env:TABCOOP_REPLAY_SAVE = ""
$env:TABCOOP_CONTROL_PORT = "27901"
$env:TABCOOP_AUTO_SHOT = "0"
$hostProc = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru

Start-Sleep -Seconds 2

Write-Host "[manual-coop] Launching CLIENT (control=27902)..."
Remove-Item Env:TABCOOP_* -ErrorAction SilentlyContinue
$env:TABCOOP_TEST = "1"
$env:TABCOOP_ROLE = "client"
$env:TABCOOP_LOG_NAME = "TABCoopMod.client.log"
$env:TABCOOP_AUTO_LOAD_SAVE = ""
$env:TABCOOP_REPLAY_SAVE = ""
$env:TABCOOP_CONTROL_PORT = "27902"
$env:TABCOOP_AUTO_SHOT = "0"
$clientProc = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru

Write-Host "[manual-coop] Waiting for Host + Client to sync into level..."
$deadline = (Get-Date).AddSeconds(60)
$hOk = $false; $cOk = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 1000
    if (-not $hOk -and (Test-Path $hostLog)) {
        if (Select-String -Path $hostLog -Pattern "Entered synced level" -Quiet -ErrorAction SilentlyContinue) { $hOk = $true }
    }
    if (-not $cOk -and (Test-Path $clientLog)) {
        if (Select-String -Path $clientLog -Pattern "Entered synced level" -Quiet -ErrorAction SilentlyContinue) { $cOk = $true }
    }
    if ($hOk -and $cOk) { break }
}

if (-not ($hOk -and $cOk)) {
    Write-Warning "[manual-coop] Timed out waiting for synced level (hOk=$hOk, cOk=$cOk). Check logs in $GameDir"
    exit 1
}

if ($TileSideBySide) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinArr {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public static IntPtr FindMainWindow(uint targetPid) {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;
        EnumWindows((h, lp) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == targetPid && IsWindowVisible(h)) {
                RECT r; GetWindowRect(h, out r);
                int area = Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
"@
    [WinArr]::SetProcessDPIAware() | Out-Null
    $hWnd = [WinArr]::FindMainWindow([uint32]$hostProc.Id)
    $cWnd = [WinArr]::FindMainWindow([uint32]$clientProc.Id)
    if ($hWnd -ne [IntPtr]::Zero -and $cWnd -ne [IntPtr]::Zero) {
        [WinArr]::ShowWindow($hWnd, 9) | Out-Null
        [WinArr]::ShowWindow($cWnd, 9) | Out-Null
        $rH = New-Object WinArr+RECT
        [WinArr]::GetWindowRect($hWnd, [ref]$rH) | Out-Null
        $w = $rH.Right - $rH.Left
        $scrW = [WinArr]::GetSystemMetrics(0)
        $scrH = [WinArr]::GetSystemMetrics(1)
        $halfW = [int]($scrW / 2)
        if ($w -le $halfW + 30) {
            [WinArr]::SetWindowPos($hWnd, [IntPtr]::Zero, 0, 200, 0, 0, 0x0001 -bor 0x0004) | Out-Null
            [WinArr]::SetWindowPos($cWnd, [IntPtr]::Zero, $w, 200, 0, 0, 0x0001 -bor 0x0004) | Out-Null
        } else {
            $halfH = [int]($halfW * 9 / 16)
            $topY = [int][Math]::Max(0, ($scrH - $halfH) / 2)
            [WinArr]::SetWindowPos($hWnd, [IntPtr]::Zero, 0, $topY, $halfW, $halfH, 0x0004) | Out-Null
            [WinArr]::SetWindowPos($cWnd, [IntPtr]::Zero, $halfW, $topY, $halfW, $halfH, 0x0004) | Out-Null
        }
        [WinArr]::SetForegroundWindow($hWnd) | Out-Null
    }
}

Write-Host "[manual-coop] Ready! Host PID=$($hostProc.Id), Client PID=$($clientProc.Id)."
Write-Host "[manual-coop] Press Space / click HUD Pause, Alt+LeftClick ping, move/build/train, +/- speed, then close windows or say 'done'."
