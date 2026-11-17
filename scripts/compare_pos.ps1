# compare_pos.ps1
# Compares per-entity position dumps (coop_pos_run1.txt vs coop_pos_run2.txt) captured at the first
# recorded tick by TABCoopMod's DeterministicReplayVerifier when TABCOOP_POS_DUMP=1.
# Reports how many entities moved differently, by how much, and which types dominate.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/compare_pos.ps1 -Run1 <path1> -Run2 <path2> [-Eps 0.01]

param(
    [Parameter(Mandatory=$true)][string]$Run1,
    [Parameter(Mandatory=$true)][string]$Run2,
    [double]$Eps = 0.01
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Run1)) { Write-Error "Missing $Run1"; exit 2 }
if (-not (Test-Path $Run2)) { Write-Error "Missing $Run2"; exit 2 }

function Read-Pos([string]$path) {
    $h = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $p = $line.Split('|')
        if ($p.Length -lt 4) { continue }
        $h[$p[0]] = [pscustomobject]@{ Id = $p[0]; X = [double]$p[1]; Y = [double]$p[2]; Type = $p[3] }
    }
    return $h
}

$a = Read-Pos $Run1
$b = Read-Pos $Run2
Write-Host "[pos] run1=$($a.Count) entities, run2=$($b.Count) entities"

$onlyA = @($a.Keys | Where-Object { -not $b.ContainsKey($_) })
$onlyB = @($b.Keys | Where-Object { -not $a.ContainsKey($_) })
if ($onlyA.Count -or $onlyB.Count) {
    Write-Host "[pos] ID set differs: onlyInRun1=$($onlyA.Count), onlyInRun2=$($onlyB.Count)"
}

$diffs = New-Object System.Collections.Generic.List[object]
$maxDx = 0.0; $maxDy = 0.0
$common = 0
foreach ($id in $a.Keys) {
    if (-not $b.ContainsKey($id)) { continue }
    $common++
    $dx = [Math]::Abs($a[$id].X - $b[$id].X)
    $dy = [Math]::Abs($a[$id].Y - $b[$id].Y)
    if ($dx -gt $Eps -or $dy -gt $Eps) {
        if ($dx -gt $maxDx) { $maxDx = $dx }
        if ($dy -gt $maxDy) { $maxDy = $dy }
        $diffs.Add([pscustomobject]@{ Id=$id; Type=$a[$id].Type; Dx=$dx; Dy=$dy; X1=$a[$id].X; Y1=$a[$id].Y; X2=$b[$id].X; Y2=$b[$id].Y })
    }
}

Write-Host "[pos] common=$common, moved(>${Eps})=$($diffs.Count), maxDx=$([Math]::Round($maxDx,4)), maxDy=$([Math]::Round($maxDy,4))"

if ($diffs.Count -gt 0) {
    Write-Host "[pos] top types by moved count:"
    $diffs | Group-Object Type | Sort-Object Count -Descending | Select-Object -First 8 | ForEach-Object {
        Write-Host ("   {0,-28} {1}" -f $_.Name, $_.Count)
    }
    Write-Host "[pos] sample (id type  dx dy  x1,y1 -> x2,y2):"
    $diffs | Sort-Object Dx -Descending | Select-Object -First 10 | ForEach-Object {
        Write-Host ("   {0} {1}  d=({2:F3},{3:F3})  ({4:F2},{5:F2})->({6:F2},{7:F2})" -f $_.Id, $_.Type, $_.Dx, $_.Dy, $_.X1, $_.Y1, $_.X2, $_.Y2)
    }
    exit 1
}

Write-Host "[pos] PASS: all common entity positions identical within ${Eps}"
exit 0
