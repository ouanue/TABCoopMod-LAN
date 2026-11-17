# run_game_with_control.ps1
# Launches TheyAreBillions with the in-process MCP control server enabled (127.0.0.1:ControlPort),
# optionally auto-loading a save so the game lands in a playable level. The replay verifier is kept OFF.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/run_game_with_control.ps1 `
#       -SavePath "$env:USERPROFILE\Documents\My Games\They Are Billions\Saves\1_Backup.zxsav"

param(
    [string]$SavePath = "",
    [int]$ControlPort = 27901,
    [string]$GameDir = "",
    [string]$ShotDir = "",
    [switch]$NoAutoLoad,
    [switch]$AutoShotOff
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
$exe = Join-Path $GameDir "TheyAreBillions.exe"
if (-not (Test-Path $exe)) { Write-Error "Game exe not found: $exe"; exit 2 }

# Screenshots go to the workspace (repo) by default so they are easy to review / read back.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
if ([string]::IsNullOrEmpty($ShotDir)) { $ShotDir = Join-Path $RepoRoot "tabcoop_shots" }
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$env:TABCOOP_CONTROL_PORT = "$ControlPort"
$env:TABCOOP_SHOT_DIR = $ShotDir
if ($AutoShotOff) { $env:TABCOOP_AUTO_SHOT = "0" } else { $env:TABCOOP_AUTO_SHOT = "1" }

Remove-Item Env:TABCOOP_TEST -ErrorAction SilentlyContinue
Remove-Item Env:TABCOOP_REPLAY_SAVE -ErrorAction SilentlyContinue

if (-not $NoAutoLoad -and $SavePath) {
    $env:TABCOOP_AUTO_LOAD_SAVE = $SavePath
} else {
    Remove-Item Env:TABCOOP_AUTO_LOAD_SAVE -ErrorAction SilentlyContinue
}

Start-Process -FilePath $exe -WorkingDirectory $GameDir
$auto = if ($SavePath -and -not $NoAutoLoad) { $SavePath } else { "no" }
Write-Host "Launched TheyAreBillions. Control port=$ControlPort  autoLoad=$auto  autoShot=$($env:TABCOOP_AUTO_SHOT)  shotDir=$ShotDir"
