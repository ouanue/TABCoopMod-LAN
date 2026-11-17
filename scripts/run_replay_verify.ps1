# run_replay_verify.ps1
# Runs the deterministic replay verifier. Default mode is "twoproc": each pass runs in a FRESH game
# process (the engine cannot reliably reload a level in place), then the two hash timelines are compared.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/run_replay_verify.ps1 `
#       -SavePath "$env:USERPROFILE\Documents\My Games\They Are Billions\Saves\test.zxsav" -Ticks 200

param(
    [Parameter(Mandatory=$true)][string]$SavePath,
    [int]$Ticks = 200,
    [ValidateSet("twoproc","inproc")][string]$RunMode = "twoproc",
    [string]$ShotCam = "",
    [switch]$RevealMap,
    [string]$GameDir = "",
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$ShotDir = Join-Path $RepoRoot "tabcoop_shots"
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

$exe = Join-Path $GameDir "TheyAreBillions.exe"
$log = Join-Path $GameDir "TABCoopMod.log"
if (-not (Test-Path $exe)) { Write-Error "Game exe not found: $exe"; exit 2 }
if (-not (Test-Path $SavePath)) { Write-Error "Save not found: $SavePath"; exit 2 }

function Stop-Game { Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500 }

function Run-Pass([int]$runIdx) {
    Stop-Game
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $env:TABCOOP_TEST = "1"
    $env:TABCOOP_AUTO_LOAD_SAVE = $SavePath
    $env:TABCOOP_REPLAY_SAVE = $SavePath
    $env:TABCOOP_REPLAY_TICKS = "$Ticks"
    $env:TABCOOP_REPLAY_RUN = "$runIdx"
    $env:TABCOOP_TICK_GATE = "always"
    $env:TABCOOP_INTERCEPT = "0"
    $env:TABCOOP_POS_DUMP = "1"
    $env:TABCOOP_SHOT_DIR = $ShotDir
    if ($ShotCam) { $env:TABCOOP_SHOT_CAM = $ShotCam } else { Remove-Item Env:TABCOOP_SHOT_CAM -ErrorAction SilentlyContinue }
    if ($RevealMap) { $env:TABCOOP_REVEAL_MAP = "1" } else { Remove-Item Env:TABCOOP_REVEAL_MAP -ErrorAction SilentlyContinue }

    Write-Host "[replay] pass $runIdx launching..."
    $proc = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $verdict = "TIMEOUT"
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 1000
        if (-not (Test-Path $log)) { continue }
        $tail = Get-Content $log -Tail 40 -ErrorAction SilentlyContinue
        if ($tail | Select-String -Pattern "ReplayVerifier FAIL|FALLBACK_REQUIRED" -Quiet) { $verdict = "FAIL"; break }
        if ($tail | Select-String -Pattern "SINGLE-PASS RUN $runIdx DONE" -Quiet) { $verdict = "DONE"; break }
    }
    Stop-Game
    Write-Host "[replay] pass $runIdx verdict: $verdict"
    return $verdict
}

if ($RunMode -eq "twoproc") {
    $v1 = Run-Pass 1
    if ($v1 -ne "DONE") { Write-Host "[replay] RESULT: FAIL (pass 1 $v1)"; exit 1 }
    $v2 = Run-Pass 2
    if ($v2 -ne "DONE") { Write-Host "[replay] RESULT: FAIL (pass 2 $v2)"; exit 1 }

    $r1 = Join-Path $GameDir "coop_replay_run1.timeline"
    $r2 = Join-Path $GameDir "coop_replay_run2.timeline"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ScriptDir "compare_hashes.ps1") -Run1 $r1 -Run2 $r2
    $hashExit = $LASTEXITCODE

    $p1 = Join-Path $GameDir "coop_pos_run1.txt"
    $p2 = Join-Path $GameDir "coop_pos_run2.txt"
    if ((Test-Path $p1) -and (Test-Path $p2)) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ScriptDir "compare_pos.ps1") -Run1 $p1 -Run2 $p2
    }
    exit $hashExit
}
else {
    # in-process double run (kept for completeness; engine reload is unreliable)
    Stop-Game
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $env:TABCOOP_TEST = "1"; $env:TABCOOP_AUTO_LOAD_SAVE = $SavePath; $env:TABCOOP_REPLAY_SAVE = $SavePath
    $env:TABCOOP_REPLAY_TICKS = "$Ticks"; $env:TABCOOP_REPLAY_RUN = "0"; $env:TABCOOP_TICK_GATE = "always"
    $proc = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds * 2)
    $verdict = "TIMEOUT"
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 1000
        if (-not (Test-Path $log)) { continue }
        $tail = Get-Content $log -Tail 40 -ErrorAction SilentlyContinue
        if ($tail | Select-String -Pattern "ReplayVerifier FAIL|FALLBACK_REQUIRED" -Quiet) { $verdict = "FAIL"; break }
        if ($tail | Select-String -Pattern "ReplayVerifier PASS" -Quiet) { $verdict = "PASS"; break }
    }
    Stop-Game
    Write-Host "[replay] inproc verdict: $verdict"
    if ($verdict -eq "PASS") { exit 0 } else { exit 1 }
}
