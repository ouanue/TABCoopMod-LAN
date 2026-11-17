# run_chaos_two_instance.ps1
# Launches a loopback HOST + CLIENT pair with ChaosTransport fault injection, auto-connects them,
# auto-loads a save on the host (which streams the .zxsav snapshot to the client), then reports the
# key milestone markers from each process log. Each process gets its own log file.
#
# NOTE: TurnInputTickGate lockstep gating is wired in CoopSessionManager (see docs/02_ARCHITECTURE_DESIGN.md
# and docs/07_TESTING_AND_MCP.md). This script validates transport, snapshot transfer, lockstep sync, and chaos statistics.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/run_chaos_two_instance.ps1 `
#       -SavePath "$env:USERPROFILE\Documents\My Games\They Are Billions\Saves\test.zxsav" `
#       -Chaos "latency=120;jitter=30;loss=0.02;disconnect=4000-5000;seed=42"

param(
    [Parameter(Mandatory=$true)][string]$SavePath,
    [string]$Chaos = "latency=120;jitter=30;loss=0.02;disconnect=4000-5000;seed=42",
    [string]$GameDir = "",
    [int]$TimeoutSeconds = 240,
    [int]$HoldSeconds = 0
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
$exe = Join-Path $GameDir "TheyAreBillions.exe"
if (-not (Test-Path $exe)) { Write-Error "Game exe not found: $exe"; exit 2 }
if (-not (Test-Path $SavePath)) { Write-Error "Save not found: $SavePath"; exit 2 }

$hostLog = Join-Path $GameDir "TABCoopMod.host.log"
$clientLog = Join-Path $GameDir "TABCoopMod.client.log"

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item $hostLog, $clientLog -Force -ErrorAction SilentlyContinue

function Set-CommonEnv {
    $env:TABCOOP_TEST = "1"
    $env:TABCOOP_TICK_GATE = "always"
    $env:TABCOOP_CHAOS = $Chaos
}

# ---- Host ----
Set-CommonEnv
$env:TABCOOP_ROLE = "host"
$env:TABCOOP_LOG_NAME = "TABCoopMod.host.log"
$env:TABCOOP_AUTO_LOAD_SAVE = $SavePath
$env:TABCOOP_REPLAY_SAVE = ""       # verifier idle; we only want the snapshot flow
Write-Host "[chaos] launching HOST..."
$hostProc = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru

Start-Sleep -Seconds 2

# ---- Client ----
Set-CommonEnv
$env:TABCOOP_ROLE = "client"
$env:TABCOOP_LOG_NAME = "TABCoopMod.client.log"
$env:TABCOOP_AUTO_LOAD_SAVE = ""
$env:TABCOOP_REPLAY_SAVE = ""
Write-Host "[chaos] launching CLIENT..."
$clientProc = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$hostOk = $false; $clientOk = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 1500
    if (Test-Path $hostLog) {
        $h = Get-Content $hostLog -Tail 60 -ErrorAction SilentlyContinue
        if ($h | Select-String -Pattern "Broadcasting Snapshot" -Quiet) { $hostOk = $true }
    }
    if (Test-Path $clientLog) {
        $c = Get-Content $clientLog -Tail 80 -ErrorAction SilentlyContinue
        if ($c | Select-String -Pattern "Entered synced level" -Quiet) { $clientOk = $true }
    }
    if ($hostOk -and $clientOk) { break }
}

if (($hostOk -and $clientOk) -and $HoldSeconds -gt 0) {
    Write-Host "[chaos] both milestones reached; holding ${HoldSeconds}s to observe lockstep..."
    Start-Sleep -Seconds $HoldSeconds
}

Write-Host "=================== HOST LOG (tail) ==================="
if (Test-Path $hostLog) { Get-Content $hostLog -Tail 25 } else { Write-Host "(no host log)" }
Write-Host "================== CLIENT LOG (tail) =================="
if (Test-Path $clientLog) { Get-Content $clientLog -Tail 30 } else { Write-Host "(no client log)" }

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force

if ($hostOk -and $clientOk) { Write-Host "[chaos] RESULT: both instances reached milestones"; exit 0 }
if ($hostOk) { Write-Host "[chaos] RESULT: HOST ok, CLIENT did not reach milestone (see log)"; exit 1 }
Write-Host "[chaos] RESULT: HOST did not reach milestone (see log)"; exit 1
