# compare_hashes.ps1
# Compares two replay timeline files (coop_replay_run1.timeline vs coop_replay_run2.timeline)
# produced by TABCoopMod's DeterministicReplayVerifier in two-process fallback mode.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/compare_hashes.ps1 `
#       -Run1 "D:\...\They Are Billions\coop_replay_run1.timeline" `
#       -Run2 "D:\...\They Are Billions\coop_replay_run2.timeline"

param(
    [Parameter(Mandatory=$true)][string]$Run1,
    [Parameter(Mandatory=$true)][string]$Run2
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Run1)) { Write-Error "Missing $Run1"; exit 2 }
if (-not (Test-Path $Run2)) { Write-Error "Missing $Run2"; exit 2 }

function Read-Timeline([string]$path) {
    Get-Content $path | Where-Object { $_.Trim().StartsWith("{") } | ForEach-Object { $_ | ConvertFrom-Json }
}

$a = @(Read-Timeline $Run1)
$b = @(Read-Timeline $Run2)
Write-Host "[compare] run1=$($a.Count) records, run2=$($b.Count) records"

$n = [Math]::Min($a.Count, $b.Count)
for ($i = 0; $i -lt $n; $i++) {
    $x = $a[$i]; $y = $b[$i]
    if ($x.hash -ne $y.hash) {
        $diffs = @()
        if ("$($x.gold),$($x.wood),$($x.stone),$($x.iron),$($x.oil)" -ne "$($y.gold),$($y.wood),$($y.stone),$($y.iron),$($y.oil)") { $diffs += "res" }
        if ($x.entities -ne $y.entities) { $diffs += "entities" }
        if ($x.pos -ne $y.pos) { $diffs += "pos" }
        if ($x.life -ne $y.life) { $diffs += "life" }
        if ($x.proj -ne $y.proj) { $diffs += "proj" }
        if ($x.prod -ne $y.prod) { $diffs += "prod" }
        if ($x.resv -ne $y.resv) { $diffs += "resv" }
        if ($diffs.Count -eq 0) { $diffs = @("hash") }
        Write-Host "[compare] FAIL at index $i (tick $($x.tick) vs $($y.tick)): $($x.hash) != $($y.hash) [$($diffs -join ',')]"
        exit 1
    }
}

if ($a.Count -ne $b.Count) {
    Write-Host "[compare] FAIL: length mismatch run1=$($a.Count) run2=$($b.Count)"
    exit 1
}

Write-Host "[compare] PASS: $n ticks identical"
exit 0
