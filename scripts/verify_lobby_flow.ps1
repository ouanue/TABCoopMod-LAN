param(
    [string]$GameDir = "",
    [string]$SaveName = "1_Backup",
    [int]$BootTimeoutSec = 45,
    [int]$LoadTimeoutSec = 60
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir

function Invoke-Control {
    param(
        [int]$Port,
        [string]$Cmd,
        [hashtable]$Params = @{}
    )
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
        if (-not $obj.ok) {
            throw "Control error ($Port/$Cmd): $($obj.error)"
        }
        return $obj.result
    }
    finally {
        $client.Close()
    }
}

function Try-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$Params = @{})
    try { return Invoke-Control -Port $Port -Cmd $Cmd -Params $Params }
    catch { return $null }
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSec = 30,
        [int]$IntervalMs = 250,
        [string]$Description = "condition"
    )
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

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$shotRoot = Join-Path (Get-Location) "tabcoop_shots"
foreach ($sub in @("lobby_host", "lobby_client")) {
    $dir = Join-Path $shotRoot $sub
    if (Test-Path $dir) { Remove-Item "$dir\*.png" -Force -ErrorAction SilentlyContinue }
    $logFile = Join-Path $GameDir "TABCoopMod.$sub.log"
    if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }
}

$exePath = Join-Path $GameDir "TheyAreBillions.exe"
$hostProc = $null
$clientProc = $null

function Start-TabInstance {
    param([string]$Role, [int]$Port)
    $roleShotDir = Join-Path $shotRoot $Role
    New-Item -ItemType Directory -Force -Path $roleShotDir | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
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

try {
    Write-Host "[1/9] Launching Host (port 27901) & Client (port 27902)..."
    $hostProc = Start-TabInstance -Role "lobby_host" -Port 27901
    $clientProc = Start-TabInstance -Role "lobby_client" -Port 27902

    Wait-Until -TimeoutSec $BootTimeoutSec -Description "Both mainMenuReady" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.mainMenuReady -and $c.mainMenuReady)
    } | Out-Null
    Write-Host "      Both reached Main Menu."

    Write-Host "[2/9] Host opening entry lobby..."
    Invoke-Control 27901 "open_lobby" | Out-Null
    Wait-Until -Description "Host lobbyWindowOpen" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        return $s.lobbyWindowOpen
    } | Out-Null
    Start-Sleep -Milliseconds 300
    Invoke-Control 27901 "screenshot" @{ tag = "02_lobby_entry" } | Out-Null

    Write-Host "[3/9] Host creating local loopback room & Client probing..."
    Invoke-Control 27901 "lobby_action" @{ action = "host_loopback" } | Out-Null
    $hs = Invoke-Control 27901 "lobby_state"
    Assert-True ($hs.isHost -and $hs.localPlayerId -eq 1 -and -not $hs.canStart) "Host should be P1 and cannot start alone"

    Invoke-Control 27902 "open_lobby" | Out-Null
    $probe = Wait-Until -TimeoutSec 10 -Description "Client probe detects local host" -Condition {
        $p = Invoke-Control 27902 "lobby_action" @{ action = "probe" }
        if ($p.online) { return $p } else { return $null }
    }
    Assert-True ($probe.players -eq 1) "Expected 1 player on host probe, got $($probe.players)"
    Start-Sleep -Milliseconds 1700
    Invoke-Control 27902 "screenshot" @{ tag = "04_client_probed" } | Out-Null

    Write-Host "[4/9] Client joining local room (expect Unready, canStart=False)..."
    Invoke-Control 27902 "lobby_action" @{ action = "join_loopback" } | Out-Null
    Wait-Until -TimeoutSec 15 -Description "Host & Client both see 2 peers" -Condition {
        $h = Invoke-Control 27901 "lobby_state"
        $c = Invoke-Control 27902 "lobby_state"
        return ($h.peers.Count -eq 2 -and $c.peers.Count -eq 2 -and $c.localPlayerId -eq 2)
    } | Out-Null
    $h = Invoke-Control 27901 "lobby_state"
    $p2 = $h.peers | Where-Object { $_.id -eq 2 } | Select-Object -First 1
    Assert-True (-not $p2.ready -and -not $h.canStart) "Client should start Unready and Host canStart must be False"
    Start-Sleep -Milliseconds 350
    Invoke-Control 27901 "screenshot" @{ tag = "05_host_client_unready" } | Out-Null
    Invoke-Control 27902 "screenshot" @{ tag = "06_client_unready" } | Out-Null

    Write-Host "[5/9] Client toggling Ready -> Host canStart becomes True..."
    Invoke-Control 27902 "lobby_action" @{ action = "toggle_ready" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Host sees Client ready & canStart=True" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        $c2 = $s.peers | Where-Object { $_.id -eq 2 } | Select-Object -First 1
        return ($s.canStart -and $c2 -and $c2.ready)
    } | Out-Null
    Start-Sleep -Milliseconds 350
    Invoke-Control 27901 "screenshot" @{ tag = "07_host_all_ready" } | Out-Null
    Invoke-Control 27902 "screenshot" @{ tag = "08_client_ready" } | Out-Null

    Write-Host "[6/9] Testing singleplayer menu reuse & dynamic random seed rolling..."
    Invoke-Control 27901 "lobby_action" @{ action = "open_new_survival" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "New Survival modal opens & lobby closes" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        return ($s.anyModalOpen -and -not $s.lobbyWindowOpen)
    } | Out-Null
    $sSeed1 = (Invoke-Control 27901 "lobby_state").sessionSeed
    Start-Sleep -Milliseconds 300
    Invoke-Control 27901 "screenshot" @{ tag = "09_host_new_survival" } | Out-Null

    Invoke-Control 27901 "lobby_action" @{ action = "close_top_modal" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Cancelling New Survival reopens lobby" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        return ($s.lobbyWindowOpen -and $s.roomPhase -eq "InLobby")
    } | Out-Null
    Start-Sleep -Milliseconds 350
    Invoke-Control 27901 "screenshot" @{ tag = "10_host_reopened" } | Out-Null

    Invoke-Control 27901 "lobby_action" @{ action = "open_new_survival" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Second New Survival modal opens" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        return ($s.anyModalOpen -and -not $s.lobbyWindowOpen)
    } | Out-Null
    $sSeed2 = (Invoke-Control 27901 "lobby_state").sessionSeed
    Write-Host "      Seed1=$sSeed1, Seed2=$sSeed2"
    Assert-True ($sSeed1 -ne 20261109 -and $sSeed2 -ne 20261109 -and $sSeed1 -ne $sSeed2) "Dynamic seeds should differ and not equal fixed test SyncSeed (seed1=$sSeed1, seed2=$sSeed2)"

    Invoke-Control 27901 "lobby_action" @{ action = "close_top_modal" } | Out-Null
    Wait-Until -TimeoutSec 10 -Description "Lobby reopens before LoadSave" -Condition {
        $s = Invoke-Control 27901 "lobby_state"
        return ($s.lobbyWindowOpen -and $s.roomPhase -eq "InLobby")
    } | Out-Null

    Write-Host "[7/9] Host loading save '$SaveName' -> verifying synced live level on both..."
    Invoke-Control 27901 "lobby_action" @{ action = "load_save"; save = $SaveName } | Out-Null
    Wait-Until -TimeoutSec $LoadTimeoutSec -IntervalMs 500 -Description "Both Host and Client reach synced level" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.inLevel -and -not $h.loading -and $c.inLevel -and -not $c.loading)
    } | Out-Null
    Start-Sleep -Milliseconds 1500
    $hg = Invoke-Control 27901 "game_state"
    $cg = Invoke-Control 27902 "game_state"
    Write-Host "      In-level: Host tick=$($hg.tick) entities=$($hg.entityCount), Client tick=$($cg.tick) entities=$($cg.entityCount)"
    Assert-True ($hg.entityCount -gt 0 -and $hg.entityCount -eq $cg.entityCount) "Host & Client entity counts should match ($($hg.entityCount) vs $($cg.entityCount))"
    Invoke-Control 27901 "screenshot" @{ tag = "11_host_ingame" } | Out-Null
    Invoke-Control 27902 "screenshot" @{ tag = "12_client_ingame" } | Out-Null

    Write-Host "[8/9] Host executing ReturnToMainMenu (Option A) -> verifying both return cleanly & reset ready..."
    Invoke-Control 27901 "lobby_action" @{ action = "return_to_menu" } | Out-Null
    Wait-Until -TimeoutSec 40 -IntervalMs 400 -Description "Both return to Main Menu with waiting room reopened" -Condition {
        $h = Try-Control 27901 "lobby_state"
        $c = Try-Control 27902 "lobby_state"
        return ($h -and $c -and $h.onMainMenu -and $h.mainMenuReady -and -not $h.inLevel -and $h.lobbyWindowOpen `
                -and $c.onMainMenu -and $c.mainMenuReady -and -not $c.inLevel -and $c.lobbyWindowOpen)
    } | Out-Null

    Start-Sleep -Milliseconds 400
    $hr = Invoke-Control 27901 "lobby_state"
    $cr = Invoke-Control 27902 "lobby_state"
    $hrP2 = $hr.peers | Where-Object { $_.id -eq 2 } | Select-Object -First 1
    Assert-True ($hr.sessionActive -and $cr.sessionActive -and $hr.peers.Count -eq 2 -and $cr.peers.Count -eq 2) "Network session should remain active with 2 peers after ReturnToMainMenu"
    Assert-True ($hrP2 -and -not $hrP2.ready -and -not $hr.canStart) "Client Ready must reset to False after ReturnToMainMenu"
    Invoke-Control 27901 "screenshot" @{ tag = "13_host_after_return" } | Out-Null
    Invoke-Control 27902 "screenshot" @{ tag = "14_client_after_return" } | Out-Null

    Write-Host "[9/9] ALL ASSERTIONS PASSED!"
}
finally {
    if ($hostProc -and -not $hostProc.HasExited) { try { $hostProc.Kill() } catch {} }
    if ($clientProc -and -not $clientProc.HasExited) { try { $clientProc.Kill() } catch {} }
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force

    foreach ($logName in @("TABCoopMod.lobby_host.log", "TABCoopMod.lobby_client.log")) {
        $p = Join-Path $GameDir $logName
        if (Test-Path $p) {
            Write-Host "`n--- $logName summary ---"
            Select-String -Path $p -Pattern "ALL BINDINGS|Classes OK|ERROR|Rolled dynamic session seed|Returning to Main Menu" | ForEach-Object { Write-Host $_.Line }
        }
    }
}
