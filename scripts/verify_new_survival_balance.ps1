param(
    [string]$GameDir = "",
    [int]$BootTimeoutSec = 45,
    [int]$LoadTimeoutSec = 90
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir

function Invoke-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$Params = @{})
    $client = New-Object System.Net.Sockets.TcpClient
    $client.ReceiveTimeout = 5000
    $client.SendTimeout = 5000
    $client.Connect("127.0.0.1", $Port)
    try {
        $stream = $client.GetStream()
        $writer = New-Object System.IO.StreamWriter($stream, [System.Text.Encoding]::UTF8)
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        $payload = @{ id = 1; tool = $Cmd; args = $Params } | ConvertTo-Json -Depth 6 -Compress
        $writer.WriteLine($payload)
        $line = $reader.ReadLine()
        if ([string]::IsNullOrEmpty($line)) { throw "Empty response from port $Port for $Cmd" }
        $obj = $line | ConvertFrom-Json
        if (-not $obj.ok) { throw "Control error ($Port/$Cmd): $($obj.error)" }
        return $obj.result
    }
    finally { $client.Close() }
}

function Try-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$Params = @{})
    try { return Invoke-Control -Port $Port -Cmd $Cmd -Params $Params }
    catch { return $null }
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSec = 30, [int]$IntervalMs = 250, [string]$Description = "condition")
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $res = & $Condition
        if ($res) { return $res }
        Start-Sleep -Milliseconds $IntervalMs
    }
    throw "Timeout waiting for: $Description (${TimeoutSec}s)"
}

function Assert-True {
    param([bool]$Cond, [string]$Message)
    if (-not $Cond) { throw "ASSERT FAILED: $Message" }
}

function Start-TabInstance {
    param([string]$Role, [int]$Port, [string]$ShotRoot)
    $roleShotDir = Join-Path $ShotRoot $Role
    New-Item -ItemType Directory -Force -Path $roleShotDir | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Join-Path $GameDir "TheyAreBillions.exe")
    $psi.WorkingDirectory = $GameDir
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["TABCOOP_CONTROL_PORT"] = "$Port"
    $psi.EnvironmentVariables["TABCOOP_AUTO_SHOT"] = "1"
    $psi.EnvironmentVariables["TABCOOP_ROLE"] = $Role
    $psi.EnvironmentVariables["TABCOOP_LOG_NAME"] = "TABCoopMod.$Role.log"
    $psi.EnvironmentVariables["TABCOOP_SHOT_DIR"] = $roleShotDir
    $psi.EnvironmentVariables.Remove("TABCOOP_TEST")
    return [System.Diagnostics.Process]::Start($psi)
}

function Launch-NewSurvivalAndWait {
    param([int]$HostPort, [int]$ClientPort, [int]$TimeoutSec, [string]$SaveName)
    Invoke-Control $HostPort "lobby_action" @{ action = "open_new_survival" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Save name InputBox open" -Condition {
        $s = Invoke-Control $HostPort "lobby_state"
        return ($s.anyModalOpen -and -not $s.lobbyWindowOpen -and $s.modalHasEditBox)
    } | Out-Null

    $conf1 = Invoke-Control $HostPort "lobby_action" @{ action = "confirm_modal"; inputText = $SaveName }
    Assert-True ($conf1.ok) "Failed to confirm save name modal ($SaveName)"

    Wait-Until -TimeoutSec 10 -Description "New Survival config window open" -Condition {
        $s = Try-Control $HostPort "lobby_state"
        return ($s -and $s.anyModalOpen -and -not $s.lobbyWindowOpen -and -not $s.modalHasEditBox)
    } | Out-Null
    Start-Sleep -Milliseconds 150
    $conf2 = Invoke-Control $HostPort "lobby_action" @{ action = "confirm_modal" }
    Assert-True ($conf2.ok) "Failed to activate Play button in New Survival modal"

    Wait-Until -TimeoutSec $TimeoutSec -IntervalMs 400 -Description "Host & Client enter generated Survival level" -Condition {
        $h = Try-Control $HostPort "lobby_state"
        $c = Try-Control $ClientPort "lobby_state"
        if ($h -and -not $h.inLevel) { Try-Control $HostPort "lobby_action" @{ action = "kick_fade" } | Out-Null }
        if ($c -and -not $c.inLevel) { Try-Control $ClientPort "lobby_action" @{ action = "kick_fade" } | Out-Null }
        return ($h -and $c -and $h.inLevel -and -not $h.loading -and $c.inLevel -and -not $c.loading)
    } | Out-Null
    Start-Sleep -Milliseconds 1500
}

function Count-PlayerUnits {
    param([int]$Port)
    $res = Invoke-Control $Port "list_entities" @{ onlyPlayer = $true; limit = 200 }
    $rangers = @($res.entities | Where-Object { $_.type -like "*Ranger*" }).Count
    $soldiers = @($res.entities | Where-Object { $_.type -like "*SoldierRegular*" }).Count
    $ccs = @($res.entities | Where-Object { $_.type -like "*CommandCenter*" }).Count
    return @{ Rangers = $rangers; Soldiers = $soldiers; CommandCenters = $ccs; Total = $res.entities.Count }
}

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$shotRoot = Join-Path (Get-Location) "tabcoop_shots"
foreach ($sub in @("bal_host", "bal_client")) {
    $dir = Join-Path $shotRoot $sub
    if (Test-Path $dir) { Remove-Item "$dir\*.png" -Force -ErrorAction SilentlyContinue }
    $logFile = Join-Path $GameDir "TABCoopMod.$sub.log"
    if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }
}

$hostProc = $null
$clientProc = $null

try {
    Write-Host "[1/6] Launching Host (27901) & Client (27902)..."
    $hostProc = Start-TabInstance -Role "bal_host" -Port 27901 -ShotRoot $shotRoot
    $clientProc = Start-TabInstance -Role "bal_client" -Port 27902 -ShotRoot $shotRoot

    Wait-Until -TimeoutSec $BootTimeoutSec -Description "Both mainMenuReady" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.mainMenuReady -and $c.mainMenuReady)
    } | Out-Null

    Write-Host "[2/6] Forming 2P lobby & testing BalancePreset toggle sync..."
    Invoke-Control 27901 "open_lobby" | Out-Null
    Invoke-Control 27902 "open_lobby" | Out-Null
    Invoke-Control 27901 "lobby_action" @{ action = "host_loopback" } | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "join_loopback" } | Out-Null
    Wait-Until -TimeoutSec 15 -Description "Both see 2 peers" -Condition {
        $h = Invoke-Control 27901 "lobby_state"
        $c = Invoke-Control 27902 "lobby_state"
        return ($h.peers.Count -eq 2 -and $c.peers.Count -eq 2)
    } | Out-Null

    $hInit = Invoke-Control 27901 "lobby_state"
    $cInit = Invoke-Control 27902 "lobby_state"
    Assert-True ($hInit.balancePreset -eq "Balanced" -and $cInit.balancePreset -eq "Balanced") "Default preset should be Balanced"

    Invoke-Control 27901 "lobby_action" @{ action = "toggle_balance_preset" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Client syncs Vanilla preset" -Condition {
        $h = Invoke-Control 27901 "lobby_state"
        $c = Invoke-Control 27902 "lobby_state"
        return ($h.balancePreset -eq "Vanilla" -and $c.balancePreset -eq "Vanilla")
    } | Out-Null

    Invoke-Control 27901 "lobby_action" @{ action = "toggle_balance_preset" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Client syncs back to Balanced preset" -Condition {
        $h = Invoke-Control 27901 "lobby_state"
        $c = Invoke-Control 27902 "lobby_state"
        return ($h.balancePreset -eq "Balanced" -and $c.balancePreset -eq "Balanced")
    } | Out-Null

    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Host canStart" -Condition {
        return (Invoke-Control 27901 "lobby_state").canStart
    } | Out-Null
    Start-Sleep -Milliseconds 350
    Invoke-Control 27901 "screenshot" @{ tag = "01_host_lobby_balanced" } | Out-Null

    Write-Host "[3/6] Starting 2P Balanced New Survival..."
    Launch-NewSurvivalAndWait -HostPort 27901 -ClientPort 27902 -TimeoutSec $LoadTimeoutSec -SaveName "coop_bal_test_balanced"

    $gsH = Invoke-Control 27901 "game_state"
    $gsC = Invoke-Control 27902 "game_state"
    $unitsH = Count-PlayerUnits -Port 27901
    $unitsC = Count-PlayerUnits -Port 27902
    Write-Host "      Balanced Host:   NCells=$($gsH.survivalNCells) levelNCells=$($gsH.levelNCells) extraW=$($gsH.commandCenterExtraWorkers) extraF=$($gsH.commandCenterExtraFood) Units=$($unitsH.Rangers)R+$($unitsH.Soldiers)S+$($unitsH.CommandCenters)CC"
    Write-Host "      Balanced Client: NCells=$($gsC.survivalNCells) levelNCells=$($gsC.levelNCells) extraW=$($gsC.commandCenterExtraWorkers) extraF=$($gsC.commandCenterExtraFood) Units=$($unitsC.Rangers)R+$($unitsC.Soldiers)S+$($unitsC.CommandCenters)CC"

    Assert-True ($gsH.survivalNCells -eq 280 -and $gsC.survivalNCells -eq 280) "Expected survivalNCells=280 on both, got H=$($gsH.survivalNCells) C=$($gsC.survivalNCells)"
    Assert-True ($gsH.commandCenterExtraWorkers -eq 2 -and $gsC.commandCenterExtraWorkers -eq 2) "Expected extraWorkers=2 on both, got H=$($gsH.commandCenterExtraWorkers) C=$($gsC.commandCenterExtraWorkers)"
    Assert-True ($gsH.commandCenterExtraFood -eq 0 -and $gsC.commandCenterExtraFood -eq 0) "Expected extraFood=0 on both"
    Assert-True ($unitsH.Rangers -eq 6 -and $unitsH.Soldiers -eq 1 -and $unitsH.CommandCenters -eq 1) "Host units should be 6R+1S+1CC"
    Assert-True ($unitsC.Rangers -eq 6 -and $unitsC.Soldiers -eq 1 -and $unitsC.CommandCenters -eq 1) "Client units should be 6R+1S+1CC"

    Write-Host "[4/6] Unpausing Balanced session & running 6s checksum stability check..."
    Invoke-Control 27901 "set_paused" @{ paused = $false } | Out-Null
    Start-Sleep -Milliseconds 6500
    $gsH2 = Invoke-Control 27901 "game_state"
    $gsC2 = Invoke-Control 27902 "game_state"
    Write-Host "      After 6.5s: Host tick=$($gsH2.tick), Client tick=$($gsC2.tick)"
    Assert-True ($gsH2.tick -gt 60 -and $gsC2.tick -gt 60) "Both instances should advance simulation ticks"
    Invoke-Control 27901 "screenshot" @{ tag = "02_balanced_ingame_host" } | Out-Null

    Write-Host "[5/6] Returning to lobby, switching to Vanilla, starting 2P Vanilla New Survival..."
    Invoke-Control 27901 "lobby_action" @{ action = "return_to_menu" } | Out-Null
    Wait-Until -TimeoutSec 20 -Description "Both back in lobby" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.lobbyWindowOpen -and $c.lobbyWindowOpen)
    } | Out-Null

    Invoke-Control 27901 "lobby_action" @{ action = "toggle_balance_preset" } | Out-Null
    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Host canStart in Vanilla" -Condition {
        $h = Invoke-Control 27901 "lobby_state"
        return ($h.canStart -and $h.balancePreset -eq "Vanilla")
    } | Out-Null

    Launch-NewSurvivalAndWait -HostPort 27901 -ClientPort 27902 -TimeoutSec $LoadTimeoutSec -SaveName "coop_bal_test_vanilla"
    $vgH = Invoke-Control 27901 "game_state"
    $vgC = Invoke-Control 27902 "game_state"
    $vuH = Count-PlayerUnits -Port 27901
    $vuC = Count-PlayerUnits -Port 27902
    Write-Host "      Vanilla Host:   NCells=$($vgH.survivalNCells) extraW=$($vgH.commandCenterExtraWorkers) extraF=$($vgH.commandCenterExtraFood) Units=$($vuH.Rangers)R+$($vuH.Soldiers)S+$($vuH.CommandCenters)CC"
    Write-Host "      Vanilla Client: NCells=$($vgC.survivalNCells) extraW=$($vgC.commandCenterExtraWorkers) extraF=$($vgC.commandCenterExtraFood) Units=$($vuC.Rangers)R+$($vuC.Soldiers)S+$($vuC.CommandCenters)CC"

    Assert-True ($vgH.survivalNCells -eq 256 -and $vgC.survivalNCells -eq 256) "Vanilla survivalNCells should be 256 on both"
    Assert-True ($vgH.commandCenterExtraWorkers -eq 0 -and $vgC.commandCenterExtraWorkers -eq 0) "Vanilla extraWorkers should be 0 on both"
    Assert-True ($vuH.Rangers -eq 4 -and $vuH.Soldiers -eq 1 -and $vuC.Rangers -eq 4 -and $vuC.Soldiers -eq 1) "Vanilla units should be 4R+1S on both"

    Write-Host "[6/6] Checking Host/Client logs for CoopBalance & StateChecksum errors..."
    $hostLog = Get-Content (Join-Path $GameDir "TABCoopMod.bal_host.log") -Raw
    $clientLog = Get-Content (Join-Path $GameDir "TABCoopMod.bal_client.log") -Raw
    Assert-True ($hostLog -match "\[CoopBalance\] Post-gen complete") "Missing [CoopBalance] Post-gen log on Host"
    Assert-True ($hostLog -notmatch "StateChecksum MISMATCH|\[ERROR\]") "Host log should contain no checksum mismatch or ERROR"
    Assert-True ($clientLog -notmatch "StateChecksum MISMATCH|\[ERROR\]") "Client log should contain no checksum mismatch or ERROR"

    Write-Host "ALL NEW SURVIVAL BALANCE CHECKS PASSED!"
}
finally {
    if ($hostProc -and -not $hostProc.HasExited) { $hostProc | Stop-Process -Force -ErrorAction SilentlyContinue }
    if ($clientProc -and -not $clientProc.HasExited) { $clientProc | Stop-Process -Force -ErrorAction SilentlyContinue }
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
    $savesDir = Join-Path $env:USERPROFILE "Documents\My Games\They Are Billions\Saves"
    if (Test-Path $savesDir) {
        Get-ChildItem $savesDir -Filter "coop_bal_test_*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
